using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SkyFlipTracker.Migrations
{
    public partial class timestampIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Flips_Timestamp",
                table: "Flips",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_FlipEvents_Timestamp",
                table: "FlipEvents",
                column: "Timestamp");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Flips_Timestamp",
                table: "Flips");

            migrationBuilder.DropIndex(
                name: "IX_FlipEvents_Timestamp",
                table: "FlipEvents");
        }
    }
}
