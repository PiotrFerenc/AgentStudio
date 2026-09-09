using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProviderHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Headers",
                table: "Providers",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Headers",
                table: "Providers");
        }
    }
}
