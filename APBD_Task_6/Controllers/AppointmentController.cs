using APBD_Task_6.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_Task_6.Controllers;
[ApiController]
[Route("api/[controller]")]
public class AppointmentController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentController(IConfiguration configuration)
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