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
        _connectionString = configuration.GetConnectionString("DefaultConnection") ??
                            throw new InvalidOperationException();
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

        const string patientCheckQuery = """
                                         SELECT IsActive FROM dbo.Patients
                                         WHERE IdPatient = @IdPatient
                                         """;
        await using (var patientCheckCommand = new SqlCommand(patientCheckQuery, connection))
        {
            patientCheckCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);

            await using var patientCheckReader = await patientCheckCommand.ExecuteReaderAsync();

            if (!await patientCheckReader.ReadAsync())
            {
                return BadRequest(new ErrorResponseDto("Patient not found"));
            }

            var isActive = patientCheckReader.GetBoolean(0);
            if (!isActive)
            {
                return BadRequest(new ErrorResponseDto("Patient is not active"));
            }
        }

        const string doctorCheckQuery = """
                                        SELECT IsActive FROM dbo.Doctors
                                        WHERE IdDoctor = @IdDoctor
                                        """;
        await using (var doctorCheckCommand = new SqlCommand(doctorCheckQuery, connection))
        {
            doctorCheckCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);

            await using var doctorCheckReader = await doctorCheckCommand.ExecuteReaderAsync();

            if (!await doctorCheckReader.ReadAsync())
            {
                return BadRequest(new ErrorResponseDto("Doctor not found"));
            }

            var isActive = doctorCheckReader.GetBoolean(0);
            if (!isActive)
            {
                return BadRequest(new ErrorResponseDto("Doctor is not active"));
            }
        }

        const string conflictQuery = """
                                     SELECT 1
                                     FROM dbo.Appointments
                                     WHERE IdDoctor = @IdDoctor
                                       AND AppointmentDate = @AppointmentDate
                                       AND Status = 'Scheduled';
                                     """;

        await using (var conflictCommand = new SqlCommand(conflictQuery, connection))
        {
            conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);

            await using var conflictReader = await conflictCommand.ExecuteReaderAsync();

            if (await conflictReader.ReadAsync())
            {
                return Conflict(new ErrorResponseDto($"Doctor already has an appointment at this time"));
            }
        }

        int newId;
        const string appointmentInsertQuery = """
                                              INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Reason, Status)
                                              OUTPUT INSERTED.IdAppointment
                                              VALUES (@IdPatient, @IdDoctor, @AppointmentDate, @Reason, 'Scheduled')
                                              """;
        await using var insertCommand = new SqlCommand(appointmentInsertQuery, connection);
        insertCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
        insertCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
        insertCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
        insertCommand.Parameters.AddWithValue("@Reason", request.Reason);

        newId = (int)(await insertCommand.ExecuteScalarAsync())!;
        return CreatedAtAction(nameof(GetAppointment), new { id = newId }, null);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAppointment([FromRoute] int id,
        [FromBody] UpdateAppointmentRequestDto request)
    {
        var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!allowedStatuses.Contains(request.Status))
        {
            return BadRequest(new ErrorResponseDto("Status must be one of: Scheduled, Completed, Cancelled"));
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        string currentStatus;
        DateTime currentAppointmentDate;
        const string checkAppointmentExistsQuery = """
                                                   SELECT Status, AppointmentDate FROM dbo.Appointments
                                                   WHERE IdAppointment = @IdAppointment
                                                   """;
        await using (var checkAppointmentCommand = new SqlCommand(checkAppointmentExistsQuery, connection))
        {
            checkAppointmentCommand.Parameters.AddWithValue("@IdAppointment", id);

            await using var reader = await checkAppointmentCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new ErrorResponseDto("Appointment not found"));
            }

            currentStatus = reader.GetString(0);
            currentAppointmentDate = reader.GetDateTime(1);
        }

        if (currentStatus == "Completed" && currentAppointmentDate != request.AppointmentDate)
        {
            return Conflict(new ErrorResponseDto("Cannot change the date of a completed appointment"));
        }

        const string patientCheckQuery = """
                                         SELECT IsActive FROM dbo.Patients
                                         WHERE IdPatient = @IdPatient
                                         """;
        await using (var patientCheckCommand = new SqlCommand(patientCheckQuery, connection))
        {
            patientCheckCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);

            await using var patientCheckReader = await patientCheckCommand.ExecuteReaderAsync();
            if (!await patientCheckReader.ReadAsync())
            {
                return BadRequest(new ErrorResponseDto("Patient not found"));
            }

            if (!patientCheckReader.GetBoolean(0))
            {
                return BadRequest(new ErrorResponseDto("Patient is not active"));
            }
        }

        const string doctorCheckQuery = """
                                        SELECT IsActive FROM dbo.Doctors
                                        WHERE IdDoctor = @IdDoctor
                                        """;
        await using (var doctorCheckCommand = new SqlCommand(doctorCheckQuery, connection))
        {
            doctorCheckCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);

            await using var doctorCheckReader = await doctorCheckCommand.ExecuteReaderAsync();
            if (!await doctorCheckReader.ReadAsync())
            {
                return BadRequest(new ErrorResponseDto("Doctor not found"));
            }

            if (!doctorCheckReader.GetBoolean(0))
            {
                return BadRequest(new ErrorResponseDto("Doctor is not active"));
            }
        }

        if (currentAppointmentDate != request.AppointmentDate)
        {
            const string conflictQuery = """
                                         SELECT 1
                                         FROM dbo.Appointments
                                         WHERE IdDoctor = @IdDoctor
                                           AND AppointmentDate = @AppointmentDate
                                           AND Status = 'Scheduled'
                                           AND IdAppointment <> @IdAppointment;
                                         """;
            await using (var conflictCommand = new SqlCommand(conflictQuery, connection))
            {
                conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
                conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
                conflictCommand.Parameters.AddWithValue("@IdAppointment", id);

                await using var conflictReader = await conflictCommand.ExecuteReaderAsync();
                if (await conflictReader.ReadAsync())
                {
                    return Conflict(new ErrorResponseDto("Doctor already has an appointment at this time"));
                }
            }
        }

        const string updateQuery = """
                                   UPDATE dbo.Appointments
                                   SET IdPatient       = @IdPatient,
                                       IdDoctor        = @IdDoctor,
                                       AppointmentDate = @AppointmentDate,
                                       Status          = @Status,
                                       Reason          = @Reason,
                                       InternalNotes   = @InternalNotes
                                   WHERE IdAppointment = @IdAppointment;
                                   """;
        await using var updateCommand = new SqlCommand(updateQuery, connection);
        updateCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
        updateCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
        updateCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
        updateCommand.Parameters.AddWithValue("@Status", request.Status);
        updateCommand.Parameters.AddWithValue("@Reason", request.Reason);
        updateCommand.Parameters.AddWithValue("@InternalNotes", (object?)request.InternalNotes ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@IdAppointment", id);

        await updateCommand.ExecuteNonQueryAsync();

        return Ok();
    }
}