using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScheduledRunAt",
                table: "Agents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ScheduleEnabled",
                table: "Agents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleInput",
                table: "Agents",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ScheduleIntervalMinutes",
                table: "Agents",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastScheduledRunAt",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ScheduleEnabled",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ScheduleInput",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ScheduleIntervalMinutes",
                table: "Agents");
        }
    }
}
