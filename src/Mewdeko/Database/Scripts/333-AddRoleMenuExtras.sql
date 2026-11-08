-- Role menu extras: groups that share a role limit across menus, temporary roles, blocked roles, self-destructing menus,
-- per-role holder caps, an accent colour for the default embed, and the grants and reverse picks they track.
-- Migration: 333-AddRoleMenuExtras.sql

CREATE TABLE IF NOT EXISTS "RoleMenuGroups"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "Name"      TEXT           NOT NULL,
    "MaxRoles"  INTEGER        NOT NULL DEFAULT 1,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "Name")
);

ALTER TABLE "RoleMenus"
    ADD COLUMN IF NOT EXISTS "TempRoleSeconds" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "RoleMenus"
    ADD COLUMN IF NOT EXISTS "BlockedRoleIds" TEXT NOT NULL DEFAULT '';
ALTER TABLE "RoleMenus"
    ADD COLUMN IF NOT EXISTS "ExpiresAt" TIMESTAMP;
ALTER TABLE "RoleMenus"
    ADD COLUMN IF NOT EXISTS "GroupId" INTEGER REFERENCES "RoleMenuGroups" ("Id") ON DELETE SET NULL;
ALTER TABLE "RoleMenus"
    ADD COLUMN IF NOT EXISTS "AccentColor" INTEGER;

ALTER TABLE "RoleMenuOptions"
    ADD COLUMN IF NOT EXISTS "TempRoleSeconds" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "RoleMenuOptions"
    ADD COLUMN IF NOT EXISTS "MaxHolders" INTEGER NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS "RoleMenuTempGrants"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "UserId"    NUMERIC(20, 0) NOT NULL,
    "RoleId"    NUMERIC(20, 0) NOT NULL,
    "MenuId"    INTEGER        NOT NULL,
    "ExpiresAt" TIMESTAMP      NOT NULL,
    UNIQUE ("GuildId", "UserId", "RoleId")
);

CREATE INDEX IF NOT EXISTS "IX_RoleMenuTempGrants_ExpiresAt" ON "RoleMenuTempGrants" ("ExpiresAt");

CREATE TABLE IF NOT EXISTS "RoleMenuReverseStates"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL,
    "UserId"    NUMERIC(20, 0) NOT NULL,
    "RoleId"    NUMERIC(20, 0) NOT NULL,
    "MenuId"    INTEGER        NOT NULL REFERENCES "RoleMenus" ("Id") ON DELETE CASCADE,
    "DateAdded" TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("MenuId", "UserId", "RoleId")
);

CREATE INDEX IF NOT EXISTS "IX_RoleMenus_ExpiresAt" ON "RoleMenus" ("ExpiresAt") WHERE "ExpiresAt" IS NOT NULL;
CREATE INDEX IF NOT EXISTS "IX_RoleMenus_GroupId" ON "RoleMenus" ("GroupId") WHERE "GroupId" IS NOT NULL;
