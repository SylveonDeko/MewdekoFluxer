-- Moving servers over from Discord: which Fluxer account belongs to which Discord account, which servers were
-- brought over, and member rows held back until their owner links an account.
-- Migration: 226-AddDiscordMigration.sql

CREATE TABLE IF NOT EXISTS "DiscordAccountLinks"
(
    "FluxerUserId"  numeric(20, 0) PRIMARY KEY,
    "DiscordUserId" numeric(20, 0) NOT NULL UNIQUE,
    "DateAdded"     timestamp without time zone NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE TABLE IF NOT EXISTS "MigratedGuilds"
(
    "Id"             serial PRIMARY KEY,
    "GuildId"        numeric(20, 0) NOT NULL,
    "DiscordGuildId" numeric(20, 0) NOT NULL,
    "MigratedBy"     numeric(20, 0) NOT NULL,
    "ChannelMap"     jsonb          NOT NULL DEFAULT '{}',
    "RoleMap"        jsonb          NOT NULL DEFAULT '{}',
    "DateAdded"      timestamp without time zone NOT NULL DEFAULT (now() AT TIME ZONE 'utc')
);

CREATE INDEX IF NOT EXISTS "IX_MigratedGuilds_GuildId" ON "MigratedGuilds" ("GuildId");

CREATE TABLE IF NOT EXISTS "MigrationPendingRows"
(
    "Id"            bigserial PRIMARY KEY,
    "MigrationId"   integer        NOT NULL REFERENCES "MigratedGuilds" ("Id") ON DELETE CASCADE,
    "DiscordUserId" numeric(20, 0) NOT NULL,
    "TableName"     text           NOT NULL,
    "OldKey"        text,
    "Row"           jsonb          NOT NULL,
    "Position"      integer        NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_MigrationPendingRows_DiscordUserId" ON "MigrationPendingRows" ("DiscordUserId");
