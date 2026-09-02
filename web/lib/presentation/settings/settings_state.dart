import 'package:equatable/equatable.dart';

/// The state every settings screen is in: the stored values, whether a read or a save is in
/// flight, and how the last save went.
///
/// Shared across the four screens because the shape genuinely is the same — and because the two
/// things worth getting right are worth getting right once. Those are [saved], which drives the
/// "Settings saved." confirmation and has to clear again as soon as the form is edited or it will
/// still be sitting there over unsaved changes; and [fieldErrors], which is what lets a validation
/// failure appear under the field that caused it.
class SettingsState<T> extends Equatable {
  const SettingsState({
    this.value,
    this.loading = true,
    this.saving = false,
    this.saved = false,
    this.error,
    this.fieldErrors = const {},
  });

  final T? value;
  final bool loading;
  final bool saving;

  /// True immediately after a successful save, and false again the moment anything is typed.
  final bool saved;

  /// A failure with no field to attach it to — a domain rule, or the request itself.
  final String? error;

  /// Field-level failures from `ValidationProblemDetails`, keyed by the property name the server
  /// used. The keys are C# property names (`ClientId`, `IntervalValue`), so a screen looks its
  /// fields up by those rather than by its own control names.
  final Map<String, List<String>> fieldErrors;

  /// The first message for [property], for a field's `errorText`.
  ///
  /// Matched case-insensitively: FluentValidation reports `ClientId` while a hand-written rule
  /// might report `clientId`, and a mismatch would silently drop the message off the field and
  /// leave the form looking as though it saved.
  String? errorFor(String property) {
    for (final entry in fieldErrors.entries) {
      if (entry.key.toLowerCase() == property.toLowerCase() && entry.value.isNotEmpty) {
        return entry.value.first;
      }
    }
    return null;
  }

  SettingsState<T> copyWith({
    T? value,
    bool? loading,
    bool? saving,
    bool? saved,
    String? error,
    Map<String, List<String>>? fieldErrors,
    bool clearError = false,
  }) =>
      SettingsState<T>(
        value: value ?? this.value,
        loading: loading ?? this.loading,
        saving: saving ?? this.saving,
        saved: saved ?? this.saved,
        error: clearError ? null : (error ?? this.error),
        fieldErrors: fieldErrors ?? this.fieldErrors,
      );

  @override
  List<Object?> get props => [value, loading, saving, saved, error, fieldErrors];
}
