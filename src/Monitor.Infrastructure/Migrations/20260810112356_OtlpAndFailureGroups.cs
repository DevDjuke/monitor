using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OtlpAndFailureGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CostUsd",
                table: "Spans",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorType",
                table: "Spans",
                type: "nvarchar(240)",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalParentSpanId",
                table: "Spans",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSpanId",
                table: "Spans",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HttpStatusCode",
                table: "Spans",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InputTokens",
                table: "Spans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "Spans",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OutputTokens",
                table: "Spans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "FailureGroupId",
                table: "Runs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "Runs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FailureGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    FailureType = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Dependency = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    MessageTemplate = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Occurrences = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FailureGroups", x => x.Id);
                });

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

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FailureGroupId",
                table: "Runs",
                column: "FailureGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_FailureGroups_Category_LastSeenAt",
                table: "FailureGroups",
                columns: new[] { "Category", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureGroups_Fingerprint",
                table: "FailureGroups",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FailureGroups_LastSeenAt",
                table: "FailureGroups",
                column: "LastSeenAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Runs_FailureGroups_FailureGroupId",
                table: "Runs",
                column: "FailureGroupId",
                principalTable: "FailureGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Runs_FailureGroups_FailureGroupId",
                table: "Runs");

            migrationBuilder.DropTable(
                name: "FailureGroups");

            migrationBuilder.DropIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans");

            migrationBuilder.DropIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs");

            migrationBuilder.DropIndex(
                name: "IX_Runs_FailureGroupId",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "ErrorType",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "ExternalParentSpanId",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "ExternalSpanId",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "HttpStatusCode",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "Spans");

            migrationBuilder.DropColumn(
                name: "FailureGroupId",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "Runs");
        }
    }
}
