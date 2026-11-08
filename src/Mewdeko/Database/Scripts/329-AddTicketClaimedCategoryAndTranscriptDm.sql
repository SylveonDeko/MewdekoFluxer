-- Ticket claimed categories and per-component transcript DMs.
-- Migration: 329-AddTicketClaimedCategoryAndTranscriptDm.sql

ALTER TABLE "PanelButtons"
    ADD COLUMN IF NOT EXISTS "ClaimedCategoryId" NUMERIC(20, 0);
ALTER TABLE "PanelButtons"
    ADD COLUMN IF NOT EXISTS "DmTranscript" BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE "SelectMenuOptions"
    ADD COLUMN IF NOT EXISTS "ClaimedCategoryId" NUMERIC(20, 0);
ALTER TABLE "SelectMenuOptions"
    ADD COLUMN IF NOT EXISTS "DmTranscript" BOOLEAN NOT NULL DEFAULT FALSE;
