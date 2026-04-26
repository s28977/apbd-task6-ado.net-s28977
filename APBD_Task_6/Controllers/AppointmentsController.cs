using APBD_Task_6.DTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_Task_6.Controllers;
[ApiController]
[Microsoft.AspNetCore.Mvc.Route("api/[controller]")]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException();
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments(
        [FromQuery] string? status,
        [FromQuery] string? patientLastName)
    {

        const string sql = """
                           SELECT
                               a.IdAppointment,
                               a.AppointmentDate,
                               a.Status,
                               a.Reason,
                               p.FirstName + N' ' + p.LastName AS PatientFullName,
                               p.Email AS PatientEmail
                           FROM dbo.Appointments a
                           JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                           WHERE (@Status IS NULL OR a.Status = @Status)
                             AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
                           ORDER BY a.AppointmentDate;
                           """;
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        
        command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@PatientLastName", (object?)patientLastName ?? DBNull.Value);
        
        await connection.OpenAsync();
        
        var results = new List<AppointmentListDto>();
        
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }
        
        
        return Ok(results);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAppointment([FromRoute] int id)
    {
        const string sql = """
                           SELECT
                               a.AppointmentDate,
                               a.Status,
                               a.Reason,
                               a.InternalNotes,
                               a.CreatedAt,
                               p.FirstName + N' ' + p.LastName AS PatientFullName,
                               p.Email AS PatientEmail,
                               p.PhoneNumber AS PatientPhoneNumber,
                               d.FirstName + N' ' + d.LastName AS DoctorFullName,
                               d.LicenseNumber AS DoctorLicenseNumber
                           FROM dbo.Appointments a
                           JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                           JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
                           WHERE a.IdAppointment = @IdAppointment;
                           """;
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@IdAppointment", id);
        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var appointmentDetailsDto = new AppointmentDetailsDto
            {
                AppointmentDate = reader.GetDateTime(0),
                Status = reader.GetString(1),
                Reason = reader.GetString(2),
                InternalNotes = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = reader.GetDateTime(4),
                PatientFullName = reader.GetString(5),
                PatientEmail = reader.GetString(6),
                PatientPhoneNumber = reader.GetString(7),
                DoctorFullName = reader.GetString(8),
                DoctorLicenseNumber = reader.GetString(9)
            };
            return Ok(appointmentDetailsDto);
        }
        return NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate < DateTime.UtcNow)
        {
            return BadRequest(new ErrorResponseDto("Appointment date is in the past"));
        }
        
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        int newId;

        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            const string sql = """
                               INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Reason, Status)
                               OUTPUT INSERTED.IdAppointment
                               VALUES (@IdPatient, @IdDoctor, @AppointmentDate, @Reason, 'Scheduled')
                               """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            command.Parameters.AddWithValue("@IdDoctor", request.IdDoctor); 
            command.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            command.Parameters.AddWithValue("@Reason", request.Reason);

            newId = (int)(await command.ExecuteScalarAsync())!;
            await transaction.CommitAsync();
        }
        
        return CreatedAtRoute(nameof(GetAppointments), new { IdAppointment = newId }, null);
    }
}