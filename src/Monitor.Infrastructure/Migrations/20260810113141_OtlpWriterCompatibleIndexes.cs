using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OtlpWriterCompatibleIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans");

            migrationBuilder.DropIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs");

            migrationBuilder.CreateIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans",
                columns: new[] { "RunId", "ExternalSpanId" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs",
                columns: new[] { "ComponentId", "TraceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans");

            migrationBuilder.DropIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs");

            migrationBuilder.CreateIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans",
                columns: new[] { "RunId", "ExternalSpanId" },
                unique: true,
                filter: "[ExternalSpanId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs",
                columns: new[] { "ComponentId", "TraceId" },
                unique: true,
                filter: "[TraceId] IS NOT NULL");
        }
    }
}
