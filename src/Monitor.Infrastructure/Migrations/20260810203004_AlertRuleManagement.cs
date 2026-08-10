using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AlertRuleManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "FailureAlertRules",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DeliverToAllEnabledDestinations",
                table: "FailureAlertRules",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "FailureAlertRules",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FailureAlertRuleDestinations",
                columns: table => new
                {
                    FailureAlertRuleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureAlertRuleDestinations", x => new { x.FailureAlertRuleId, x.DestinationId });
                    table.ForeignKey(
                        name: "FK_FailureAlertRuleDestinations_AlertDeliveryDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDeliveryDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FailureAlertRuleDestinations_FailureAlertRules_FailureAlertRuleId",
                        column: x => x.FailureAlertRuleId,
                        principalTable: "FailureAlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_IsDeleted_Enabled_LastEvaluatedAt",
                table: "FailureAlertRules",
                columns: new[] { "IsDeleted", "Enabled", "LastEvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRuleDestinations_DestinationId",
                table: "FailureAlertRuleDestinations",
                column: "DestinationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FailureAlertRuleDestinations");

            migrationBuilder.DropIndex(
                name: "IX_FailureAlertRules_IsDeleted_Enabled_LastEvaluatedAt",
                table: "FailureAlertRules");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "FailureAlertRules");

            migrationBuilder.DropColumn(
                name: "DeliverToAllEnabledDestinations",
                table: "FailureAlertRules");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "FailureAlertRules");
        }
    }
}
