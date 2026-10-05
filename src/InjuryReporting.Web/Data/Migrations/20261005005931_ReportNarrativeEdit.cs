using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InjuryReporting.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportNarrativeEdit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NarrativeEditedUtc",
                table: "Reports",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NarrativeEditedUtc",
                table: "Reports");
        }
    }
}
