-- Optional temporary text channel for each temporary voice channel. Off until a community turns it on.
-- Migration: 330-AddCustomVoiceTextChannels.sql

ALTER TABLE "CustomVoiceConfig"
    ADD COLUMN IF NOT EXISTS "CreateTextChannel" BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE "CustomVoiceChannel"
    ADD COLUMN IF NOT EXISTS "TextChannelId" NUMERIC(20, 0);
