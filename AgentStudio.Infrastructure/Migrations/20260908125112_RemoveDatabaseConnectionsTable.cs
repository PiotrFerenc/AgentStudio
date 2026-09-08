using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDatabaseConnectionsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionString = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReadOnly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseConnections_Name",
                table: "DatabaseConnections",
                column: "Name",
                unique: true);
        }
    }
}
