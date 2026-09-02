/// Helpers for reading the API's JSON.
///
/// These exist mostly to be tolerant in the two places where the wire format is not uniform, both
/// of which are deliberate on the server's side rather than accidental:
///
///   * **Enums arrive as either a name or an ordinal.** `UpgradePathStatus`, `UpgradeMethod` and
///     `ScriptApprovalPublishOutcome` carry converters that write their names; `HostStatus`,
///     `AiProvider`, `AuthProvider` and `PatchingTimeUnit` have none, so System.Text.Json writes
///     their ordinals. That asymmetry must *not* be fixed by turning on a global string-enum
///     converter: all three Rust agents parse some of these by ordinal — see
///     `clients/*/src/policy.rs`, which reads `interval_unit` as a `u8` — so flipping it would
///     break the fleet. [enumFromJson] reads whichever form arrives instead.
///   * **Dates are ISO 8601 with an offset** (`DateTimeOffset`), which [dateTimeFromJson] parses
///     and converts to local time. The server does not know the visitor's timezone, so every
///     timestamp is rendered in the browser — the same job the `data-utc` script did.
library;

/// Reads an enum sent as either its name or its ordinal.
///
/// [values] must be in declaration order, matching the C# enum, since that is what an ordinal
/// indexes into. Returns [fallback] for anything unrecognised rather than throwing: a value added
/// to a server-side enum should degrade to "unknown" on an older client, not blank the screen.
T enumFromJson<T>(Object? raw, List<T> values, List<String> names, T fallback) {
  if (raw is num) {
    final index = raw.toInt();
    return index >= 0 && index < values.length ? values[index] : fallback;
  }
  if (raw is String) {
    final index = names.indexWhere((n) => n.toLowerCase() == raw.toLowerCase());
    return index >= 0 ? values[index] : fallback;
  }
  return fallback;
}

DateTime? dateTimeFromJson(Object? raw) {
  if (raw is! String || raw.isEmpty) return null;
  return DateTime.tryParse(raw)?.toLocal();
}

DateTime dateTimeRequiredFromJson(Object? raw) =>
    dateTimeFromJson(raw) ?? DateTime.fromMillisecondsSinceEpoch(0);

List<String> stringListFromJson(Object? raw) =>
    raw is List ? raw.map((e) => e.toString()).toList(growable: false) : const [];

List<T> listFromJson<T>(Object? raw, T Function(Map<String, dynamic>) item) => raw is List
    ? raw.whereType<Map<String, dynamic>>().map(item).toList(growable: false)
    : const [];
