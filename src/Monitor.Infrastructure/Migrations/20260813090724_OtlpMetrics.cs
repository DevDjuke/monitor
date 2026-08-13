using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Monitor.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OtlpMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Temporality = table.Column<int>(type: "int", nullable: false),
                    IsMonotonic = table.Column<bool>(type: "bit", nullable: false),
                    HasRecordedValue = table.Column<bool>(type: "bit", nullable: false),
                    StartTimestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NumericValue = table.Column<double>(type: "float", nullable: true),
                    Count = table.Column<decimal>(type: "decimal(20,0)", precision: 20, scale: 0, nullable: true),
                    Sum = table.Column<double>(type: "float", nullable: true),
                    Min = table.Column<double>(type: "float", nullable: true),
                    Max = table.Column<double>(type: "float", nullable: true),
                    Scale = table.Column<int>(type: "int", nullable: true),
                    ZeroCount = table.Column<decimal>(type: "decimal(20,0)", precision: 20, scale: 0, nullable: true),
                    ZeroThreshold = table.Column<double>(type: "float", nullable: true),
                    BucketCountsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExplicitBoundsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PositiveBucketsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NegativeBucketsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QuantilesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResourceAttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetricMetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExemplarsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScopeName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    ScopeVersion = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    ResourceSchemaUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ScopeSchemaUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Flags = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricPoints_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ComponentId_Name_Timestamp",
                table: "MetricPoints",
                columns: new[] { "ComponentId", "Name", "Timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ComponentId_Timestamp",
                table: "MetricPoints",
                columns: new[] { "ComponentId", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_DedupeKey",
                table: "MetricPoints",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_Kind_Timestamp",
                table: "MetricPoints",
                columns: new[] { "Kind", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_Name_Timestamp",
                table: "MetricPoints",
                columns: new[] { "Name", "Timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_Timestamp",
                table: "MetricPoints",
                column: "Timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricPoints");
        }
    }
}
