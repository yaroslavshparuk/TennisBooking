using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TennisBooking.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceReminderJobIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttendanceReminder24hJobId",
                table: "BookingCancellationLinks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttendanceReminder2hJobId",
                table: "BookingCancellationLinks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttendanceReminder24hJobId",
                table: "BookingCancellationLinks");

            migrationBuilder.DropColumn(
                name: "AttendanceReminder2hJobId",
                table: "BookingCancellationLinks");
        }
    }
}
