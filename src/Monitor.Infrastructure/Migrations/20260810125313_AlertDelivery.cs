using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlertDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertDestinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ProtectedSigningSecret = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_AlertDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_FailureAlertEvents_AlertEventId",
                        column: x => x.AlertEventId,
                        principalTable: "FailureAlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FailureAlertRoutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureAlertRoutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FailureAlertRoutes_AlertDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FailureAlertRoutes_FailureAlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "FailureAlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_AlertEventId_DestinationId",
                table: "AlertDeliveries",
                columns: new[] { "AlertEventId", "DestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_DestinationId_CreatedAt",
                table: "AlertDeliveries",
                columns: new[] { "DestinationId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_LeaseId",
                table: "AlertDeliveries",
                column: "LeaseId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveries_Status_NextAttemptAt_LeaseExpiresAt",
                table: "AlertDeliveries",
                columns: new[] { "Status", "NextAttemptAt", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDestinations_Enabled_Kind",
                table: "AlertDestinations",
                columns: new[] { "Enabled", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRoutes_AlertRuleId_DestinationId",
                table: "FailureAlertRoutes",
                columns: new[] { "AlertRuleId", "DestinationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRoutes_DestinationId",
                table: "FailureAlertRoutes",
                column: "DestinationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertDeliveries");

            migrationBuilder.DropTable(
                name: "FailureAlertRoutes");

            migrationBuilder.DropTable(
                name: "AlertDestinations");
        }
    }
}
