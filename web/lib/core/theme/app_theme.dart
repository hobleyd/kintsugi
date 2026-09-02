import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import 'kintsugi_palette.dart';

/// Builds the two themes from a [KintsugiPalette].
///
/// Three type families, used for the same things the stylesheet used them for: Orbitron for
/// headings, nav, labels, status chips and buttons; Rajdhani for body text; Share Tech Mono for
/// anything that is literally code — scripts, hashes, serial numbers, and every text field, since
/// what gets typed into them is a repository slug, a client ID or a URL.
abstract final class AppTheme {
  static ThemeData dark() => _build(KintsugiPalette.dark, Brightness.dark);

  static ThemeData light() => _build(KintsugiPalette.light, Brightness.light);

  /// The display face: uppercase, tracked out. Exposed because several widgets need it directly
  /// rather than through a [TextTheme] slot — status chips and nav entries, mainly.
  static TextStyle display({
    required Color color,
    double size = 11.5,
    FontWeight weight = FontWeight.w700,
    double letterSpacing = 1.15,
  }) =>
      GoogleFonts.orbitron(
        color: color,
        fontSize: size,
        fontWeight: weight,
        letterSpacing: letterSpacing,
      );

  /// The monospace face, for code and for field contents.
  static TextStyle mono({required Color color, double size = 14.4}) =>
      GoogleFonts.shareTechMono(color: color, fontSize: size);

  static ThemeData _build(KintsugiPalette palette, Brightness brightness) {
    final body = GoogleFonts.rajdhaniTextTheme().apply(
      bodyColor: palette.text,
      displayColor: palette.text,
    );

    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      scaffoldBackgroundColor: palette.background,
      canvasColor: palette.background,
      colorScheme: ColorScheme.fromSeed(
        seedColor: palette.neon,
        brightness: brightness,
      ).copyWith(
        surface: palette.background,
        primary: palette.neon,
        error: palette.red,
      ),
      extensions: [palette],
      textTheme: body.copyWith(
        // h1: the page title. 1.9rem uppercase Orbitron in the stylesheet.
        headlineLarge: GoogleFonts.orbitron(
          color: palette.neon,
          fontSize: 30,
          fontWeight: FontWeight.w700,
          letterSpacing: 1.8,
        ),
        // h2: a section heading within a page.
        titleLarge: GoogleFonts.orbitron(
          color: palette.neonSoft,
          fontSize: 17.6,
          fontWeight: FontWeight.w700,
          letterSpacing: 0.9,
        ),
        // h3.
        titleMedium: GoogleFonts.orbitron(
          color: palette.neonDim,
          fontSize: 11.5,
          fontWeight: FontWeight.w700,
          letterSpacing: 1.15,
        ),
        // Table headers and form labels share one slot: both are 0.68rem tracked Orbitron in
        // --neon-dim, and keeping them one style is what stops the two drifting apart.
        labelLarge: GoogleFonts.orbitron(
          color: palette.neonDim,
          fontSize: 10.9,
          fontWeight: FontWeight.w600,
          letterSpacing: 1.09,
        ),
        bodyMedium: body.bodyMedium?.copyWith(fontSize: 15.2, color: palette.text),
        // .hint / .muted-text.
        bodySmall: body.bodySmall?.copyWith(fontSize: 12.8, color: palette.muted),
      ),
      dividerTheme: DividerThemeData(color: palette.border, thickness: 1, space: 1),
      iconTheme: IconThemeData(color: palette.neonDim, size: 18),
      inputDecorationTheme: InputDecorationTheme(
        isDense: true,
        filled: true,
        fillColor: palette.accentWash(0.04),
        hintStyle: AppTheme.mono(color: palette.muted),
        contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        border: _fieldBorder(palette.border),
        enabledBorder: _fieldBorder(palette.border),
        focusedBorder: _fieldBorder(palette.neon),
        errorBorder: _fieldBorder(palette.red),
        focusedErrorBorder: _fieldBorder(palette.red),
      ),
      checkboxTheme: CheckboxThemeData(
        fillColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? palette.neon : Colors.transparent,
        ),
        checkColor: WidgetStatePropertyAll(palette.background),
        side: BorderSide(color: palette.border),
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.all(Radius.circular(2))),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: palette.backgroundAlt,
          border: Border.all(color: palette.border),
          borderRadius: BorderRadius.circular(3),
        ),
        textStyle: body.bodySmall?.copyWith(color: palette.text),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: palette.neon,
        linearTrackColor: palette.accentWash(0.08),
      ),
      scrollbarTheme: ScrollbarThemeData(
        thumbColor: WidgetStatePropertyAll(palette.accentWash(0.35)),
      ),
    );
  }

  static OutlineInputBorder _fieldBorder(Color color) => OutlineInputBorder(
        borderRadius: BorderRadius.circular(3),
        borderSide: BorderSide(color: color),
      );
}
