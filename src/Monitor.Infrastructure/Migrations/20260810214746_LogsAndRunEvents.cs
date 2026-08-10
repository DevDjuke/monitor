using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LogsAndRunEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LogEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SpanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalTraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ExternalSpanId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ExternalRecordId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DedupeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    SeverityText = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    EventName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    MessageTemplate = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    PropertiesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExceptionType = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    ExceptionMessage = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExceptionStackTrace = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LogEvents_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_LogEvents_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LogEvents_Spans_SpanId",
                        column: x => x.SpanId,
                        principalTable: "Spans",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_ComponentId_Timestamp",
                table: "LogEvents",
                columns: new[] { "ComponentId", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_DedupeKey",
                table: "LogEvents",
                column: "DedupeKey");

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_ExternalRecordId",
                table: "LogEvents",
                column: "ExternalRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_RunId_Timestamp",
                table: "LogEvents",
                columns: new[] { "RunId", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_SpanId_Timestamp",
                table: "LogEvents",
                columns: new[] { "SpanId", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_LogEvents_Timestamp",
                table: "LogEvents",
                column: "Timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LogEvents");
        }
    }
}
