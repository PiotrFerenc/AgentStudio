using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFormFieldExtensionsAndResultBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FormResultMarkdown",
                table: "AgentVersions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FormResultMode",
                table: "AgentVersions",
                type: "text",
                nullable: false,
                defaultValue: "inline");

            migrationBuilder.AddColumn<string>(
                name: "FormResultTarget",
                table: "AgentVersions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FormResultMarkdown",
                table: "AgentVersions");

            migrationBuilder.DropColumn(
                name: "FormResultMode",
                table: "AgentVersions");

            migrationBuilder.DropColumn(
                name: "FormResultTarget",
                table: "AgentVersions");
        }
    }
}
