-- Tracks when a member last filed a member report so reports can be rate limited.
-- Migration: 338-AddReportSupport.sql

CREATE TABLE IF NOT EXISTS "ModReportCooldowns"
(
    "GuildId"      NUMERIC(20, 0) NOT NULL,
    "UserId"       NUMERIC(20, 0) NOT NULL,
    "LastReportAt" TIMESTAMP      NOT NULL,
    PRIMARY KEY ("GuildId", "UserId")
);
