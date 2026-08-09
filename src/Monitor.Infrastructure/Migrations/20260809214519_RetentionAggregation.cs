using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetentionAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AggregatedAt",
                table: "Runs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RunAggregates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BucketStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TotalRuns = table.Column<long>(type: "bigint", nullable: false),
                    SuccessRuns = table.Column<long>(type: "bigint", nullable: false),
                    FailedRuns = table.Column<long>(type: "bigint", nullable: false),
                    CancelledRuns = table.Column<long>(type: "bigint", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<double>(type: "float", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    MinDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    MaxDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    FirstStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_AggregatedAt",
                table: "Runs",
                column: "AggregatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status_CompletedAt_AggregatedAt",
                table: "Runs",
                columns: new[] { "Status", "CompletedAt", "AggregatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunAggregates_BucketStart",
                table: "RunAggregates",
                column: "BucketStart");

            migrationBuilder.CreateIndex(
                name: "IX_RunAggregates_BucketStart_ComponentId_Model",
                table: "RunAggregates",
                columns: new[] { "BucketStart", "ComponentId", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunAggregates_ComponentId_BucketStart",
                table: "RunAggregates",
                columns: new[] { "ComponentId", "BucketStart" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunAggregates");

            migrationBuilder.DropIndex(
                name: "IX_Runs_AggregatedAt",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_Status_CompletedAt_AggregatedAt",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "AggregatedAt",
                table: "Runs");
        }
    }
}
