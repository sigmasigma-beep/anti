// ================================================================
// PlayFab Cloud Scripts
// Paste ALL of this into your PlayFab title's CloudScript editor.
// Game Manager → Build → CloudScript → paste → Save and deploy.
// ================================================================

// ----------------------------------------------------------------
// HELPER: get the role level number for a PlayFab item ID
// Must match your Unity RoleLevel() function exactly.
// ----------------------------------------------------------------
function getRoleLevel(itemId) {
    switch (itemId) {
        case "Owner":   return 5;
        case "Admin":   return 4;
        case "Manager": return 3;
        case "Mod":     return 2;
        case "YT":      return 1;
        default:        return 0;
    }
}

// ----------------------------------------------------------------
// HELPER: get the highest role level from a player's inventory
// Returns { level: int, roleName: string }
// ----------------------------------------------------------------
function getPlayerRole(playFabId) {
    var inv = server.GetUserInventory({ PlayFabId: playFabId });

    var highestLevel = 0;
    var highestRole  = "Player";
    var roleItems    = ["Owner", "Admin", "Manager", "Mod", "YT"];

    for (var i = 0; i < inv.Inventory.length; i++) {
        var itemId = inv.Inventory[i].ItemId;
        var level  = getRoleLevel(itemId);
        if (level > highestLevel) {
            highestLevel = level;
            highestRole  = itemId;
        }
    }

    return { level: highestLevel, roleName: highestRole };
}

// ----------------------------------------------------------------
// VerifyKick
// Called by the TARGET player's client when it receives a KickPlayer RPC.
// Verifies server-side that the kicker actually outranks the target.
// Returns: { approved: bool, reason: string }
// ----------------------------------------------------------------
handlers.VerifyKick = function(args) {
    var kickerPlayFabId = args.kickerPlayFabId;
    var targetPlayFabId = args.targetPlayFabId;

    // Basic validation
    if (!kickerPlayFabId || !targetPlayFabId) {
        return { approved: false, reason: "Missing PlayFab IDs" };
    }

    // A player cannot kick themselves
    if (kickerPlayFabId === targetPlayFabId) {
        return { approved: false, reason: "Cannot kick self" };
    }

    // Fetch both players' roles from inventory (server-side, cannot be faked)
    var kickerRole = getPlayerRole(kickerPlayFabId);
    var targetRole = getPlayerRole(targetPlayFabId);

    log.info("[VerifyKick] Kicker: " + kickerPlayFabId + " (" + kickerRole.roleName + " L" + kickerRole.level + ")"
           + " | Target: " + targetPlayFabId + " (" + targetRole.roleName + " L" + targetRole.level + ")");

    // Kicker must strictly outrank the target
    if (kickerRole.level <= 0) {
        return { approved: false, reason: "Kicker has no staff role" };
    }

    if (kickerRole.level <= targetRole.level) {
        return {
            approved: false,
            reason: "Kicker role '" + kickerRole.roleName
                  + "' does not outrank target role '" + targetRole.roleName + "'"
        };
    }

    // Log the approved kick to PlayStream so you have an audit trail
    server.WriteTitleEvent({
        EventName: "kick_approved",
        Body: {
            kicker_id:   kickerPlayFabId,
            kicker_role: kickerRole.roleName,
            target_id:   targetPlayFabId,
            target_role: targetRole.roleName
        }
    });

    return { approved: true, reason: "OK" };
};

// ----------------------------------------------------------------
// IncrementReportAndAutoBan
// Called when a player reports another.
// Increments a report counter in the target's internal title data.
// Auto-bans if the threshold is reached.
// Returns: { reportCount: int, banned: bool }
// ----------------------------------------------------------------
handlers.IncrementReportAndAutoBan = function(args) {
    var targetId   = args.targetId;
    var targetName = args.targetName || "Unknown";
    var reporterId = args.reporterId;

    var BAN_THRESHOLD = 5; // ban after this many unique reports

    if (!targetId) {
        return { reportCount: 0, banned: false, error: "Missing targetId" };
    }

    // Staff can never be auto-banned by reports
    var targetRole = getPlayerRole(targetId);
    if (targetRole.level > 0) {
        log.info("[AutoBan] Skipping auto-ban check for staff: " + targetId);
        return { reportCount: 0, banned: false, reason: "Target is staff" };
    }

    // Fetch current report data
    var data = server.GetUserInternalData({
        PlayFabId: targetId,
        Keys: ["ReportCount", "Reporters"]
    });

    var reportCount = 0;
    var reporters   = {};

    if (data.Data["ReportCount"]) {
        reportCount = parseInt(data.Data["ReportCount"].Value) || 0;
    }

    if (data.Data["Reporters"]) {
        try { reporters = JSON.parse(data.Data["Reporters"].Value); }
        catch (e) { reporters = {}; }
    }

    // Only count one report per unique reporter (prevents one person farming bans)
    if (reporters[reporterId]) {
        log.info("[AutoBan] " + reporterId + " already reported " + targetId);
        return { reportCount: reportCount, banned: false, reason: "Already reported" };
    }

    // Record this reporter and increment count
    reporters[reporterId] = true;
    reportCount++;

    server.UpdateUserInternalData({
        PlayFabId: targetId,
        Data: {
            ReportCount: reportCount.toString(),
            Reporters:   JSON.stringify(reporters),
            LastReportedName: targetName
        }
    });

    log.info("[AutoBan] " + targetId + " now has " + reportCount + " reports (threshold: " + BAN_THRESHOLD + ")");

    // Auto-ban if threshold reached
    if (reportCount >= BAN_THRESHOLD) {
        server.BanUsers({
            Bans: [{
                PlayFabId: targetId,
                Reason:    "Auto-banned: " + reportCount + " player reports",
                // DurationInHours: 24  // uncomment for a temp ban instead of permanent
            }]
        });

        server.WriteTitleEvent({
            EventName: "auto_ban_triggered",
            Body: {
                target_id:    targetId,
                target_name:  targetName,
                report_count: reportCount
            }
        });

        log.info("[AutoBan] Banned: " + targetId);
        return { reportCount: reportCount, banned: true };
    }

    return { reportCount: reportCount, banned: false };
};

// ----------------------------------------------------------------
// ResetReportCount  (admin utility — call from Game Manager or admin tool)
// Clears report data for a player (e.g. after a false-report wave).
// ----------------------------------------------------------------
handlers.ResetReportCount = function(args) {
    var targetId = args.targetId;

    if (!targetId) return { success: false, error: "Missing targetId" };

    server.UpdateUserInternalData({
        PlayFabId: targetId,
        Data: {
            ReportCount: "0",
            Reporters:   "{}"
        }
    });

    log.info("[ResetReportCount] Cleared reports for: " + targetId);
    return { success: true };
};

// ----------------------------------------------------------------
// GetPlayerRole  (utility — useful for debugging from Game Manager)
// Returns the server-verified role for any player ID.
// ----------------------------------------------------------------
handlers.GetPlayerRole = function(args) {
    var playFabId = args.playFabId;
    if (!playFabId) return { error: "Missing playFabId" };

    var role = getPlayerRole(playFabId);
    return { playFabId: playFabId, role: role.roleName, level: role.level };
};

// ----------------------------------------------------------------
// ReportMovementViolation
// Called by Player.cs when it detects a movement cheat locally.
// Logs the violation, increments a counter, and auto-bans on threshold.
// Returns: { logged: bool, violationCount: int, banned: bool }
// ----------------------------------------------------------------
handlers.ReportMovementViolation = function(args) {
    var playFabId = args.playFabId;
    var reason    = args.reason    || "unknown";
    var position  = args.position  || "unknown";
    var velocity  = args.velocity  || "unknown";

    if (!playFabId || playFabId === "unknown") {
        return { logged: false, error: "Missing playFabId" };
    }

    var VIOLATION_BAN_THRESHOLD = 10; // ban after this many movement violations

    // Staff are never auto-banned by movement reports (in case of edge cases)
    var role = getPlayerRole(playFabId);
    if (role.level > 0) {
        log.info("[MovementAC] Skipping auto-ban for staff: " + playFabId);
        return { logged: true, violationCount: 0, banned: false, reason: "Staff exempt" };
    }

    // Fetch existing violation count
    var data = server.GetUserInternalData({
        PlayFabId: playFabId,
        Keys: ["MovementViolations", "MovementViolationLog"]
    });

    var violationCount = 0;
    var violationLog   = [];

    if (data.Data["MovementViolations"]) {
        violationCount = parseInt(data.Data["MovementViolations"].Value) || 0;
    }

    if (data.Data["MovementViolationLog"]) {
        try { violationLog = JSON.parse(data.Data["MovementViolationLog"].Value); }
        catch (e) { violationLog = []; }
    }

    violationCount++;

    // Keep last 20 log entries
    violationLog.push({
        reason:    reason,
        position:  position,
        velocity:  velocity,
        time:      new Date().toUTCString()
    });
    if (violationLog.length > 20) violationLog.shift();

    server.UpdateUserInternalData({
        PlayFabId: playFabId,
        Data: {
            MovementViolations:    violationCount.toString(),
            MovementViolationLog:  JSON.stringify(violationLog)
        }
    });

    log.info("[MovementAC] " + playFabId + " violation #" + violationCount + ": " + reason);

    // Write to PlayStream for audit trail
    server.WriteTitleEvent({
        EventName: "movement_violation",
        Body: {
            player_id: playFabId,
            reason:    reason,
            position:  position,
            velocity:  velocity,
            count:     violationCount
        }
    });

    // Auto-ban on threshold
    if (violationCount >= VIOLATION_BAN_THRESHOLD) {
        server.BanUsers({
            Bans: [{
                PlayFabId: playFabId,
                Reason:    "Auto-banned: " + violationCount + " movement violations. Last: " + reason
            }]
        });

        log.info("[MovementAC] Auto-banned: " + playFabId);
        return { logged: true, violationCount: violationCount, banned: true };
    }

    return { logged: true, violationCount: violationCount, banned: false };
};

// ----------------------------------------------------------------
// ResetMovementViolations  (admin utility)
// Clears movement violation data for a player (e.g. false positives).
// ----------------------------------------------------------------
handlers.ResetMovementViolations = function(args) {
    var playFabId = args.playFabId;
    if (!playFabId) return { success: false, error: "Missing playFabId" };

    server.UpdateUserInternalData({
        PlayFabId: playFabId,
        Data: {
            MovementViolations:   "0",
            MovementViolationLog: "[]"
        }
    });

    log.info("[MovementAC] Reset violations for: " + playFabId);
    return { success: true };
};
