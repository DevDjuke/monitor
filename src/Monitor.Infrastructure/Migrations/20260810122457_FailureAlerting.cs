using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FailureAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FailureAlertRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FailureGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Threshold = table.Column<int>(type: "int", nullable: false),
                    WindowMinutes = table.Column<int>(type: "int", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTriggeredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTriggeredRunSequence = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureAlertRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FailureAlertRules_FailureGroups_FailureGroupId",
                        column: x => x.FailureGroupId,
                        principalTable: "FailureGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FailureAlertEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FailureGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WindowEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OccurrencesInWindow = table.Column<long>(type: "bigint", nullable: false),
                    Threshold = table.Column<int>(type: "int", nullable: false),
                    LatestRunSequence = table.Column<long>(type: "bigint", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureAlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FailureAlertEvents_FailureAlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "FailureAlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FailureAlertEvents_FailureGroups_FailureGroupId",
                        column: x => x.FailureGroupId,
                        principalTable: "FailureGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FailureGroupId_CompletedAt_Sequence",
                table: "Runs",
                columns: new[] { "FailureGroupId", "CompletedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertEvents_AcknowledgedAt",
                table: "FailureAlertEvents",
                column: "AcknowledgedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertEvents_AlertRuleId_TriggeredAt",
                table: "FailureAlertEvents",
                columns: new[] { "AlertRuleId", "TriggeredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertEvents_FailureGroupId_TriggeredAt",
                table: "FailureAlertEvents",
                columns: new[] { "FailureGroupId", "TriggeredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertEvents_TriggeredAt",
                table: "FailureAlertEvents",
                column: "TriggeredAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_Enabled_LastEvaluatedAt",
                table: "FailureAlertRules",
                columns: new[] { "Enabled", "LastEvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_FailureGroupId_Enabled",
                table: "FailureAlertRules",
                columns: new[] { "FailureGroupId", "Enabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FailureAlertEvents");

            migrationBuilder.DropTable(
                name: "FailureAlertRules");

            migrationBuilder.DropIndex(
                name: "IX_Runs_FailureGroupId_CompletedAt_Sequence",
                table: "Runs");
        }
    }
}
