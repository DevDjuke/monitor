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
            migrationBuilder.CreateSequence(
                name: "RunSequence");

            migrationBuilder.CreateTable(
                name: "AlertDeliveryDestinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    EndpointUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailureAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailure = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDeliveryDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorType = table.Column<int>(type: "int", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TargetName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Components",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Environment = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    ControlState = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Status = table.Column<int>(type: "int", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Components", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Surface = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    QueryString = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedViews_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ComponentCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TargetRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveryAttempts = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComponentCommands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComponentCommands_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ComponentIngestionCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComponentIngestionCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComponentIngestionCredentials_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                    DeliverToAllEnabledDestinations = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
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
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "NEXT VALUE FOR [RunSequence]"),
                    ComponentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FailureGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AggregatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<double>(type: "float", nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Components_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "Components",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Runs_FailureGroups_FailureGroupId",
                        column: x => x.FailureGroupId,
                        principalTable: "FailureGroups",
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

            migrationBuilder.CreateTable(
                name: "Spans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentSpanId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalSpanId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    ExternalParentSpanId = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorType = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CostUsd = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Spans_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
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

            migrationBuilder.CreateTable(
                name: "AlertDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
                    table.PrimaryKey("PK_AlertDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertDeliveries_AlertDeliveryDestinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "AlertDeliveryDestinations",
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
                name: "IX_AlertDeliveries_Status_NextAttemptAt",
                table: "AlertDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryDestinations_Enabled_Kind",
                table: "AlertDeliveryDestinations",
                columns: new[] { "Enabled", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertDeliveryDestinations_Name",
                table: "AlertDeliveryDestinations",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "Action", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ActorType_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "ActorType", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetType_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "TargetType", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TargetType_TargetId_OccurredAt",
                table: "AuditEvents",
                columns: new[] { "TargetType", "TargetId", "OccurredAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ComponentCommands_ComponentId_Status_AvailableAt_CreatedAt",
                table: "ComponentCommands",
                columns: new[] { "ComponentId", "Status", "AvailableAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComponentCommands_CreatedAt",
                table: "ComponentCommands",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ComponentCommands_LeaseExpiresAt",
                table: "ComponentCommands",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ComponentCommands_Status_ExpiresAt",
                table: "ComponentCommands",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComponentCommands_TargetRunId",
                table: "ComponentCommands",
                column: "TargetRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ComponentIngestionCredentials_ComponentId_RevokedAt",
                table: "ComponentIngestionCredentials",
                columns: new[] { "ComponentId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ComponentIngestionCredentials_KeyId",
                table: "ComponentIngestionCredentials",
                column: "KeyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComponentIngestionCredentials_LastUsedAt",
                table: "ComponentIngestionCredentials",
                column: "LastUsedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Components_Slug_Environment",
                table: "Components",
                columns: new[] { "Slug", "Environment" },
                unique: true);

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
                name: "IX_FailureAlertRuleDestinations_DestinationId",
                table: "FailureAlertRuleDestinations",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_Enabled_LastEvaluatedAt",
                table: "FailureAlertRules",
                columns: new[] { "Enabled", "LastEvaluatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_FailureGroupId_Enabled",
                table: "FailureAlertRules",
                columns: new[] { "FailureGroupId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_FailureAlertRules_IsDeleted_Enabled_LastEvaluatedAt",
                table: "FailureAlertRules",
                columns: new[] { "IsDeleted", "Enabled", "LastEvaluatedAt" });

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

            migrationBuilder.CreateIndex(
                name: "IX_Runs_AggregatedAt",
                table: "Runs",
                column: "AggregatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ComponentId_ExternalId",
                table: "Runs",
                columns: new[] { "ComponentId", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ComponentId_TraceId",
                table: "Runs",
                columns: new[] { "ComponentId", "TraceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FailureGroupId",
                table: "Runs",
                column: "FailureGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_FailureGroupId_CompletedAt_Sequence",
                table: "Runs",
                columns: new[] { "FailureGroupId", "CompletedAt", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Sequence",
                table: "Runs",
                column: "Sequence",
                unique: true,
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Runs_StartedAt",
                table: "Runs",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_Status_CompletedAt_AggregatedAt",
                table: "Runs",
                columns: new[] { "Status", "CompletedAt", "AggregatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_UserId_IsPinned_UpdatedAt",
                table: "SavedViews",
                columns: new[] { "UserId", "IsPinned", "UpdatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_UserId_Surface_NameKey",
                table: "SavedViews",
                columns: new[] { "UserId", "Surface", "NameKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Spans_RunId_ExternalSpanId",
                table: "Spans",
                columns: new[] { "RunId", "ExternalSpanId" });

            migrationBuilder.CreateIndex(
                name: "IX_Spans_RunId_StartedAt",
                table: "Spans",
                columns: new[] { "RunId", "StartedAt" });

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
                name: "AlertDeliveries");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ComponentCommands");

            migrationBuilder.DropTable(
                name: "ComponentIngestionCredentials");

            migrationBuilder.DropTable(
                name: "FailureAlertRuleDestinations");

            migrationBuilder.DropTable(
                name: "LogEvents");

            migrationBuilder.DropTable(
                name: "MetricPoints");

            migrationBuilder.DropTable(
                name: "RunAggregates");

            migrationBuilder.DropTable(
                name: "SavedViews");

            migrationBuilder.DropTable(
                name: "UsageBudgetAlertDeliveries");

            migrationBuilder.DropTable(
                name: "UsageBudgetDestinations");

            migrationBuilder.DropTable(
                name: "FailureAlertEvents");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "Spans");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "UsageBudgetAlertEvents");

            migrationBuilder.DropTable(
                name: "AlertDeliveryDestinations");

            migrationBuilder.DropTable(
                name: "FailureAlertRules");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "UsageBudgets");

            migrationBuilder.DropTable(
                name: "FailureGroups");

            migrationBuilder.DropTable(
                name: "Components");

            migrationBuilder.DropSequence(
                name: "RunSequence");
        }
    }
}
