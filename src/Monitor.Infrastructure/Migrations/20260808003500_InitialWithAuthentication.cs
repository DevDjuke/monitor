using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Monitor.Infrastructure.Migrations;

[DbContext(typeof(MonitorDbContext))]
[Migration("20260808003500_InitialWithAuthentication")]
public sealed class InitialWithAuthentication : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // This first migration is intentionally idempotent. Early development builds used
        // EnsureCreated(), so CREATE TABLE/INDEX IF NOT EXISTS lets an existing monitor.db
        // adopt migrations without deleting its telemetry data.
        migrationBuilder.Sql(
            """
            CREATE TABLE IF NOT EXISTS "AspNetRoles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetRoles" PRIMARY KEY,
                "Name" TEXT NULL,
                "NormalizedName" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "AspNetUsers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetUsers" PRIMARY KEY,
                "UserName" TEXT NULL,
                "NormalizedUserName" TEXT NULL,
                "Email" TEXT NULL,
                "NormalizedEmail" TEXT NULL,
                "EmailConfirmed" INTEGER NOT NULL,
                "PasswordHash" TEXT NULL,
                "SecurityStamp" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "PhoneNumber" TEXT NULL,
                "PhoneNumberConfirmed" INTEGER NOT NULL,
                "TwoFactorEnabled" INTEGER NOT NULL,
                "LockoutEnd" TEXT NULL,
                "LockoutEnabled" INTEGER NOT NULL,
                "AccessFailedCount" INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Components" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Components" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Slug" TEXT NOT NULL,
                "Type" INTEGER NOT NULL,
                "Environment" TEXT NOT NULL,
                "Version" TEXT NULL,
                "Enabled" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "LastHeartbeatAt" INTEGER NULL,
                "LastRunAt" INTEGER NULL,
                "CreatedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AspNetRoleClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY AUTOINCREMENT,
                "RoleId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "AspNetUserClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "AspNetUserLogins" (
                "LoginProvider" TEXT NOT NULL,
                "ProviderKey" TEXT NOT NULL,
                "ProviderDisplayName" TEXT NULL,
                "UserId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
                CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "AspNetUserRoles" (
                "UserId" TEXT NOT NULL,
                "RoleId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
                CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "AspNetUserTokens" (
                "UserId" TEXT NOT NULL,
                "LoginProvider" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Value" TEXT NULL,
                CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
                CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "Runs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Runs" PRIMARY KEY,
                "ComponentId" TEXT NOT NULL,
                "ExternalId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Trigger" TEXT NULL,
                "Model" TEXT NULL,
                "Status" INTEGER NOT NULL,
                "StartedAt" INTEGER NOT NULL,
                "CompletedAt" INTEGER NULL,
                "InputTokens" INTEGER NOT NULL,
                "OutputTokens" INTEGER NOT NULL,
                "CostUsd" REAL NOT NULL,
                "InputJson" TEXT NULL,
                "OutputJson" TEXT NULL,
                "Error" TEXT NULL,
                CONSTRAINT "FK_Runs_Components_ComponentId" FOREIGN KEY ("ComponentId") REFERENCES "Components" ("Id") ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS "Spans" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Spans" PRIMARY KEY,
                "RunId" TEXT NOT NULL,
                "ParentSpanId" TEXT NULL,
                "Name" TEXT NOT NULL,
                "Kind" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "StartedAt" INTEGER NOT NULL,
                "CompletedAt" INTEGER NULL,
                "AttributesJson" TEXT NULL,
                "Error" TEXT NULL,
                CONSTRAINT "FK_Spans_Runs_RunId" FOREIGN KEY ("RunId") REFERENCES "Runs" ("Id") ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");
            CREATE INDEX IF NOT EXISTS "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
            CREATE UNIQUE INDEX IF NOT EXISTS "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");
            CREATE INDEX IF NOT EXISTS "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
            CREATE INDEX IF NOT EXISTS "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Components_Slug_Environment" ON "Components" ("Slug", "Environment");
            CREATE INDEX IF NOT EXISTS "IX_Runs_ComponentId_ExternalId" ON "Runs" ("ComponentId", "ExternalId");
            CREATE INDEX IF NOT EXISTS "IX_Runs_StartedAt" ON "Runs" ("StartedAt");
            CREATE INDEX IF NOT EXISTS "IX_Spans_RunId_StartedAt" ON "Spans" ("RunId", "StartedAt");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS "AspNetRoleClaims";
            DROP TABLE IF EXISTS "AspNetUserClaims";
            DROP TABLE IF EXISTS "AspNetUserLogins";
            DROP TABLE IF EXISTS "AspNetUserRoles";
            DROP TABLE IF EXISTS "AspNetUserTokens";
            DROP TABLE IF EXISTS "Spans";
            DROP TABLE IF EXISTS "Runs";
            DROP TABLE IF EXISTS "AspNetRoles";
            DROP TABLE IF EXISTS "AspNetUsers";
            DROP TABLE IF EXISTS "Components";
            """);
    }
}
