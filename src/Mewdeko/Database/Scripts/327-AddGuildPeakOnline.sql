-- Peak online tracking for the peak online stat channel.
-- Migration: 327-AddGuildPeakOnline.sql

CREATE TABLE IF NOT EXISTS "GuildPeakOnline"
(
    "Id"        SERIAL PRIMARY KEY,
    "GuildId"   NUMERIC(20, 0) NOT NULL UNIQUE,
    "PeakCount" INTEGER        NOT NULL DEFAULT 0,
    "PeakAt"    TIMESTAMP      NOT NULL DEFAULT NOW()
);
