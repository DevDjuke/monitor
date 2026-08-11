using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BudgetsAndUsagePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageBudgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Environment = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    Period = table.Column<int>(type: "int", nullable: false),
                    CostLimitUsd = table.Column<double>(type: "float", nullable: true),
                    TokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    WarningPercent = table.Column<int>(type: "int", nullable: false),
                    CriticalPercent = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeliverToAllEnabledDestinations = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastEvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CurrentPeriodStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTriggeredLevel = table.Column<int>(type: "int", nullable: true),
                    LastObservedCostUsd = table.Column<double>(type: "float", nullable: false),
                    LastObservedTokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageBudgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageBudgets_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsageBudgetAlertEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsageBudgetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedCostUsd = table.Column<double>(type: "float", nullable: false),
                    ObservedTokens = table.Column<long>(type: "bigint", nullable: false),
                    UtilizationPercent = table.Column<double>(type: "float", nullable: false),
                    CostLimitUsd = table.Column<double>(type: "float", nullable: true),
                    TokenLimit = table.Column<long>(type: "bigint", nullable: true),
                    WarningPercent = table.Column<int>(type: "int", nullable: false),
                    CriticalPercent = table.Column<int>(type: "int", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageBudgetAlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageBudgetAlertEvents_UsageBudgets_UsageBudgetId",
                        column: x => x.UsageBudgetId,
                        principalTable: "UsageBudgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsageBudgetDestinations",
                columns: table => new
                {
                    UsageBudgetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageBudgetDestinations", x => new { x.UsageBudgetId, x.DestinationId });
                    table.ForeignKey(
                        name: "FK_UsageBudgetDestinations_AlertDeliveryDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDeliveryDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageBudgetDestinations_UsageBudgets_UsageBudgetId",
                        column: x => x.UsageBudgetId,
                        principalTable: "UsageBudgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageBudgetAlertDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BudgetAlertEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResponseStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageBudgetAlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageBudgetAlertDeliveries_AlertDeliveryDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDeliveryDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UsageBudgetAlertDeliveries_UsageBudgetAlertEvents_BudgetAlertEventId",
                        column: x => x.BudgetAlertEventId,
                        principalTable: "UsageBudgetAlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertDeliveries_BudgetAlertEventId_DestinationId",
                table: "UsageBudgetAlertDeliveries",
                columns: new[] { "BudgetAlertEventId", "DestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertDeliveries_DestinationId_CreatedAt",
                table: "UsageBudgetAlertDeliveries",
                columns: new[] { "DestinationId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertDeliveries_Status_NextAttemptAt",
                table: "UsageBudgetAlertDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertEvents_AcknowledgedAt",
                table: "UsageBudgetAlertEvents",
                column: "AcknowledgedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertEvents_TriggeredAt",
                table: "UsageBudgetAlertEvents",
                column: "TriggeredAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetAlertEvents_UsageBudgetId_PeriodStart_Level",
                table: "UsageBudgetAlertEvents",
                columns: new[] { "UsageBudgetId", "PeriodStart", "Level" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgetDestinations_DestinationId",
                table: "UsageBudgetDestinations",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgets_ComponentId_Environment_Model_Period",
                table: "UsageBudgets",
                columns: new[] { "ComponentId", "Environment", "Model", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageBudgets_IsDeleted_Enabled_LastEvaluatedAt",
                table: "UsageBudgets",
                columns: new[] { "IsDeleted", "Enabled", "LastEvaluatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageBudgetAlertDeliveries");

            migrationBuilder.DropTable(
                name: "UsageBudgetDestinations");

            migrationBuilder.DropTable(
                name: "UsageBudgetAlertEvents");

            migrationBuilder.DropTable(
                name: "UsageBudgets");
        }
    }
}
