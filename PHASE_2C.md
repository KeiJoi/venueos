# Phase 2C: Announcements

`communication.announcements` adapts the safe parts of ShoutRunner: chat-message presets, ordered actions, action delays, repeat intervals, cancellation and pacing. It uses the existing `SchedulerService` to schedule actions and `ChatCommandService` to dispatch the resulting command; it does not create a second scheduler or direct game-chat execution path.

Each action specifies one safe game channel (`/shout`, `/yell`, `/say`, or `/party`) and a message. Invalid/empty messages and invalid delays are rejected before a run starts. `AnnouncementService` implements the narrow `IAnnouncementService` interface, which future modules can depend on instead of formatting their own scheduled game commands.

Settings use the existing venue/module/schema payload key under `communication.announcements`. A venue transition stops the active run before loading the destination settings, so queued work is cancelled rather than leaking messages into the newly active venue.

Teleport, world visit, data-center visit and Lifestream IPC are intentionally not referenced by VenueOS Announcements. They may only return later as a separately reviewed optional capability.
