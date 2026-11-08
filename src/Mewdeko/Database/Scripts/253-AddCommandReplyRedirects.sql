-- Command reply redirects.
-- Migration: 253-AddCommandReplyRedirects.sql

CREATE TABLE IF NOT EXISTS "CommandReplyRedirects"
(
    "Id"            SERIAL PRIMARY KEY,
    "GuildId"       NUMERIC(20, 0) NOT NULL,
    "Target"        TEXT           NOT NULL,
    "IsModule"      BOOLEAN        NOT NULL DEFAULT FALSE,
    "ChannelId"     NUMERIC(20, 0) NOT NULL,
    "DeleteInvoker" BOOLEAN        NOT NULL DEFAULT FALSE,
    "DateAdded"     TIMESTAMP      NOT NULL DEFAULT NOW(),
    UNIQUE ("GuildId", "IsModule", "Target")
);
