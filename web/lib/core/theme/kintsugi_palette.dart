import 'package:flutter/material.dart';

/// The colour tokens the whole UI is built from, carried on [ThemeData] as an extension.
///
/// These are the same semantic names the stylesheet this app replaced used — `--neon`, `--muted`,
/// `--panel` and the rest — kept rather than renamed, because the two themes below are the same
/// two palettes and having them line up makes a colour question answerable by comparing one file
/// against the other.
///
/// Widgets read tokens from here rather than hard-coding a colour so that the light theme is a
/// single palette swap, exactly as `:root[data-theme="light"]` was. A widget that reaches for a
/// literal colour is a widget that only works in one theme, and the bug does not show up until
/// someone toggles.
@immutable
class KintsugiPalette extends ThemeExtension<KintsugiPalette> {
  const KintsugiPalette({
    required this.background,
    required this.backgroundAlt,
    required this.neon,
    required this.neonSoft,
    required this.neonDim,
    required this.pink,
    required this.green,
    required this.red,
    required this.amber,
    required this.text,
    required this.muted,
    required this.border,
    required this.panel,
    required this.glowsEnabled,
  });

  final Color background;
  final Color backgroundAlt;
  final Color neon;
  final Color neonSoft;
  final Color neonDim;
  final Color pink;
  final Color green;
  final Color red;
  final Color amber;
  final Color text;
  final Color muted;
  final Color border;
  final Color panel;

  /// Whether to draw the neon halos.
  ///
  /// False in the light theme, and not as a stylistic preference: a blurred halo of a *dark*
  /// accent on a near-white page reads as a smudge rather than a glow. The stylesheet handled this
  /// by redefining every `--glow-*` token to `none`; this one flag stands in for all of them, so
  /// glow-drawing widgets ask here instead of each deciding for itself.
  final bool glowsEnabled;

  /// The dark theme — the default, and the one the product is designed in.
  static const dark = KintsugiPalette(
    background: Color(0xFF050810),
    backgroundAlt: Color(0xFF0A0F1E),
    neon: Color(0xFF00E5FF),
    neonSoft: Color(0xFF7DF9FF),
    neonDim: Color(0xFF0891B2),
    pink: Color(0xFFFF2BD6),
    green: Color(0xFF39FF88),
    red: Color(0xFFFF2F5B),
    amber: Color(0xFFFFB703),
    text: Color(0xFFD9F8FF),
    muted: Color(0xFF5F89A3),
    border: Color(0x4700E5FF),
    panel: Color(0xD1060C18),
    glowsEnabled: true,
  );

  static const light = KintsugiPalette(
    background: Color(0xFFF4F7FA),
    backgroundAlt: Color(0xFFFFFFFF),
    neon: Color(0xFF0E7490),
    neonSoft: Color(0xFF0C5A70),
    neonDim: Color(0xFF3B8BA3),
    pink: Color(0xFFA21CAF),
    green: Color(0xFF15803D),
    red: Color(0xFFB91C1C),
    amber: Color(0xFFB45309),
    text: Color(0xFF10222B),
    muted: Color(0xFF5A7684),
    border: Color(0x380E7490),
    panel: Color(0xE6FFFFFF),
    glowsEnabled: false,
  );

  /// A translucent wash of the accent, for the tints the stylesheet wrote as
  /// `rgba(var(--accent-rgb), <alpha>)`.
  Color accentWash(double opacity) => neon.withValues(alpha: opacity);

  /// The colour a status chip uses, keyed by the status strings the API sends.
  ///
  /// Keyed on the server's own `statusKey` (see `UpgradePathSummaryDto.StatusKey`) and on
  /// `HostStatus`, so a status the server adds shows up as [muted] rather than as a crash.
  Color forStatusKey(String statusKey) => switch (statusKey) {
        'up-to-date' || 'online' => green,
        'update-available' || 'review-sign' => amber,
        'check-failed' || 'offline' => red,
        'not-found' || 'decommissioned' => amber,
        _ => muted,
      };

  @override
  KintsugiPalette copyWith({
    Color? background,
    Color? backgroundAlt,
    Color? neon,
    Color? neonSoft,
    Color? neonDim,
    Color? pink,
    Color? green,
    Color? red,
    Color? amber,
    Color? text,
    Color? muted,
    Color? border,
    Color? panel,
    bool? glowsEnabled,
  }) =>
      KintsugiPalette(
        background: background ?? this.background,
        backgroundAlt: backgroundAlt ?? this.backgroundAlt,
        neon: neon ?? this.neon,
        neonSoft: neonSoft ?? this.neonSoft,
        neonDim: neonDim ?? this.neonDim,
        pink: pink ?? this.pink,
        green: green ?? this.green,
        red: red ?? this.red,
        amber: amber ?? this.amber,
        text: text ?? this.text,
        muted: muted ?? this.muted,
        border: border ?? this.border,
        panel: panel ?? this.panel,
        glowsEnabled: glowsEnabled ?? this.glowsEnabled,
      );

  @override
  KintsugiPalette lerp(KintsugiPalette? other, double t) {
    if (other == null) return this;
    return KintsugiPalette(
      background: Color.lerp(background, other.background, t)!,
      backgroundAlt: Color.lerp(backgroundAlt, other.backgroundAlt, t)!,
      neon: Color.lerp(neon, other.neon, t)!,
      neonSoft: Color.lerp(neonSoft, other.neonSoft, t)!,
      neonDim: Color.lerp(neonDim, other.neonDim, t)!,
      pink: Color.lerp(pink, other.pink, t)!,
      green: Color.lerp(green, other.green, t)!,
      red: Color.lerp(red, other.red, t)!,
      amber: Color.lerp(amber, other.amber, t)!,
      text: Color.lerp(text, other.text, t)!,
      muted: Color.lerp(muted, other.muted, t)!,
      border: Color.lerp(border, other.border, t)!,
      panel: Color.lerp(panel, other.panel, t)!,
      // Not interpolated: a half-drawn glow mid-transition is worse than switching it at the end.
      glowsEnabled: t < 0.5 ? glowsEnabled : other.glowsEnabled,
    );
  }
}

/// Reads the palette off the nearest theme.
extension KintsugiPaletteContext on BuildContext {
  KintsugiPalette get palette =>
      Theme.of(this).extension<KintsugiPalette>() ?? KintsugiPalette.dark;
}
