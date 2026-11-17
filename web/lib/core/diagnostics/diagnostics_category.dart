/// What a run concluded about one row, as the Diagnostics page names its tabs.
///
/// The keys are the server's `RunRowOutcome` names with the first letter lowered. They are strings
/// rather than an enum on purpose: a line is saved in the browser with its category, so one saved
/// by a build that knew an outcome this one does not must still read — as an unlabelled tab, not as
/// a crash. See [diagnosticsCategoryKey].
const diagnosticsCategoryOrder = [
  // "Find Upgrade Paths".
  'resolved',
  'noKnownPath',
  'failed',
  'alreadyKnown',
  // "Check for Updates".
  'updated',
  'unchanged',
  'nothingToCheck',
];

/// The tab's name. An unknown key is shown as it is rather than hidden.
String diagnosticsCategoryLabel(String key) => switch (key) {
      'resolved' => 'Resolved',
      'noKnownPath' => 'No known path',
      'failed' => 'Failed',
      'alreadyKnown' => 'Already known',
      'updated' => 'Updated',
      'unchanged' => 'Unchanged',
      'nothingToCheck' => 'Nothing to check',
      _ => key,
    };

/// Reads the server's outcome name — `NoKnownPath` — into a key, ignoring case. Null for one this
/// client does not know, which leaves the line uncategorised rather than inventing a tab for it.
String? diagnosticsCategoryKey(String? raw) {
  if (raw == null) return null;
  for (final key in diagnosticsCategoryOrder) {
    if (key.toLowerCase() == raw.toLowerCase()) return key;
  }
  return null;
}
