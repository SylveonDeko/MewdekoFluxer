-- Role pings on repeaters.
-- Migration: 254-AddRepeaterPingRole.sql

ALTER TABLE "GuildRepeater"
    ADD COLUMN IF NOT EXISTS "PingRoleId" NUMERIC(20, 0);
