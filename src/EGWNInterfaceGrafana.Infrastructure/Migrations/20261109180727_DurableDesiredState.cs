using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EGWNInterfaceGrafana.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DurableDesiredState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Completed",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Completed",
                table: "Events");
        }
    }
}
