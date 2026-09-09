using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEnvironmentVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EnvironmentVariables",
                table: "Agents",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentVariables",
                table: "Agents");
        }
    }
}
