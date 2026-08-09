using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRunSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "RunSequence");

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                table: "Runs",
                type: "bigint",
                nullable: false,
                defaultValueSql: "NEXT VALUE FOR [RunSequence]");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Sequence",
                table: "Runs",
                column: "Sequence",
                unique: true,
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Runs_Sequence",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "Runs");

            migrationBuilder.DropSequence(
                name: "RunSequence");
        }
    }
}
