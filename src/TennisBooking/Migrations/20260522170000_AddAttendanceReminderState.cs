using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TennisBooking.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceReminderState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttendanceReminder24hSentAtUtc",
                table: "BookingCancellationLinks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttendanceReminder2hSentAtUtc",
                table: "BookingCancellationLinks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttendanceReminder24hSentAtUtc",
                table: "BookingCancellationLinks");

            migrationBuilder.DropColumn(
                name: "AttendanceReminder2hSentAtUtc",
                table: "BookingCancellationLinks");
        }
    }
}
