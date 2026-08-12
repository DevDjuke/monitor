using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations;

[DbContext(typeof(MonitorDbContext))]
[Migration("20260812231000_UsageBudgetAutomatedActions")]
public sealed class UsageBudgetAutomatedActions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UsageBudgetEnforcementPolicies",
            columns: table => new
            {
                UsageBudgetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CriticalAction = table.Column<int>(type: "int", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UsageBudgetEnforcementPolicies", x => x.UsageBudgetId);
                table.ForeignKey(
                    name: "FK_UsageBudgetEnforcementPolicies_UsageBudgets_UsageBudgetId",
                    column: x => x.UsageBudgetId,
                    principalTable: "UsageBudgets",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint(
                    name: "CK_UsageBudgetEnforcementPolicies_CriticalAction",
                    sql: "[CriticalAction] IN (1, 2)");
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "UsageBudgetEnforcementPolicies");
    }
}
