import 'package:flutter/material.dart';

/// Design tokens for WordOS.
///
/// Everything visual references these values — no raw colors, paddings or radii
/// are allowed to appear inline in feature code.
class AppSpacing {
  const AppSpacing._();

  static const double xxs = 4;
  static const double xs = 8;
  static const double sm = 12;
  static const double md = 16;
  static const double lg = 24;
  static const double xl = 32;
  static const double xxl = 48;

  /// Standard horizontal page gutter.
  static const EdgeInsets page = EdgeInsets.symmetric(horizontal: md);
}

class AppRadii {
  const AppRadii._();

  static const Radius sm = Radius.circular(10);
  static const Radius md = Radius.circular(16);
  static const Radius lg = Radius.circular(22);
  static const Radius xl = Radius.circular(28);

  static const BorderRadius cardBorder = BorderRadius.all(lg);
  static const BorderRadius fieldBorder = BorderRadius.all(md);
  static const BorderRadius chipBorder = BorderRadius.all(sm);
  static const BorderRadius sheetBorder =
      BorderRadius.vertical(top: Radius.circular(28));
}

class AppDurations {
  const AppDurations._();

  static const Duration fast = Duration(milliseconds: 150);
  static const Duration normal = Duration(milliseconds: 250);
  static const Duration slow = Duration(milliseconds: 400);
}

/// Brand and semantic colors that are not part of the Material [ColorScheme].
class AppColors {
  const AppColors._();

  static const Color brand = Color(0xFF4F46E5);
  static const Color brandDark = Color(0xFF8B87FF);

  // Per-skill accents — the visual identity of the five skills.
  static const Color reading = Color(0xFF4F46E5);
  static const Color listening = Color(0xFF0EA5E9);
  static const Color speaking = Color(0xFFF43F5E);
  static const Color writing = Color(0xFFF59E0B);
  static const Color spelling = Color(0xFF10B981);
  static const Color review = Color(0xFF8B5CF6);

  static const Color successLight = Color(0xFF15803D);
  static const Color successDark = Color(0xFF4ADE80);
  static const Color warningLight = Color(0xFFB45309);
  static const Color warningDark = Color(0xFFFBBF24);
  static const Color dangerLight = Color(0xFFB91C1C);
  static const Color dangerDark = Color(0xFFF87171);
}

/// Semantic tokens that vary by brightness, exposed through [ThemeData.extensions].
@immutable
class AppPalette extends ThemeExtension<AppPalette> {
  const AppPalette({
    required this.success,
    required this.successSurface,
    required this.warning,
    required this.warningSurface,
    required this.danger,
    required this.dangerSurface,
    required this.border,
    required this.subtleSurface,
    required this.canvas,
    required this.skillReading,
    required this.skillListening,
    required this.skillSpeaking,
    required this.skillWriting,
    required this.skillSpelling,
    required this.review,
  });

  final Color success;
  final Color successSurface;
  final Color warning;
  final Color warningSurface;
  final Color danger;
  final Color dangerSurface;
  final Color border;
  final Color subtleSurface;
  final Color canvas;
  final Color skillReading;
  final Color skillListening;
  final Color skillSpeaking;
  final Color skillWriting;
  final Color skillSpelling;
  final Color review;

  static const AppPalette light = AppPalette(
    success: AppColors.successLight,
    successSurface: Color(0xFFE8F7ED),
    warning: AppColors.warningLight,
    warningSurface: Color(0xFFFDF3E3),
    danger: AppColors.dangerLight,
    dangerSurface: Color(0xFFFDECEC),
    border: Color(0xFFE3E5EF),
    subtleSurface: Color(0xFFF2F3F9),
    canvas: Color(0xFFF7F8FC),
    skillReading: AppColors.reading,
    skillListening: AppColors.listening,
    skillSpeaking: AppColors.speaking,
    skillWriting: AppColors.writing,
    skillSpelling: AppColors.spelling,
    review: AppColors.review,
  );

  static const AppPalette dark = AppPalette(
    success: AppColors.successDark,
    successSurface: Color(0xFF12291B),
    warning: AppColors.warningDark,
    warningSurface: Color(0xFF2C2311),
    danger: AppColors.dangerDark,
    dangerSurface: Color(0xFF2D1618),
    border: Color(0xFF262A38),
    subtleSurface: Color(0xFF171A24),
    canvas: Color(0xFF0E1015),
    skillReading: Color(0xFF8B87FF),
    skillListening: Color(0xFF56C2F5),
    skillSpeaking: Color(0xFFFF7A94),
    skillWriting: Color(0xFFFBBF24),
    skillSpelling: Color(0xFF34D399),
    review: Color(0xFFA78BFA),
  );

  @override
  AppPalette copyWith({
    Color? success,
    Color? successSurface,
    Color? warning,
    Color? warningSurface,
    Color? danger,
    Color? dangerSurface,
    Color? border,
    Color? subtleSurface,
    Color? canvas,
    Color? skillReading,
    Color? skillListening,
    Color? skillSpeaking,
    Color? skillWriting,
    Color? skillSpelling,
    Color? review,
  }) {
    return AppPalette(
      success: success ?? this.success,
      successSurface: successSurface ?? this.successSurface,
      warning: warning ?? this.warning,
      warningSurface: warningSurface ?? this.warningSurface,
      danger: danger ?? this.danger,
      dangerSurface: dangerSurface ?? this.dangerSurface,
      border: border ?? this.border,
      subtleSurface: subtleSurface ?? this.subtleSurface,
      canvas: canvas ?? this.canvas,
      skillReading: skillReading ?? this.skillReading,
      skillListening: skillListening ?? this.skillListening,
      skillSpeaking: skillSpeaking ?? this.skillSpeaking,
      skillWriting: skillWriting ?? this.skillWriting,
      skillSpelling: skillSpelling ?? this.skillSpelling,
      review: review ?? this.review,
    );
  }

  @override
  AppPalette lerp(ThemeExtension<AppPalette>? other, double t) {
    if (other is! AppPalette) return this;
    Color c(Color a, Color b) => Color.lerp(a, b, t)!;
    return AppPalette(
      success: c(success, other.success),
      successSurface: c(successSurface, other.successSurface),
      warning: c(warning, other.warning),
      warningSurface: c(warningSurface, other.warningSurface),
      danger: c(danger, other.danger),
      dangerSurface: c(dangerSurface, other.dangerSurface),
      border: c(border, other.border),
      subtleSurface: c(subtleSurface, other.subtleSurface),
      canvas: c(canvas, other.canvas),
      skillReading: c(skillReading, other.skillReading),
      skillListening: c(skillListening, other.skillListening),
      skillSpeaking: c(skillSpeaking, other.skillSpeaking),
      skillWriting: c(skillWriting, other.skillWriting),
      skillSpelling: c(skillSpelling, other.skillSpelling),
      review: c(review, other.review),
    );
  }
}

extension AppThemeX on BuildContext {
  ThemeData get theme => Theme.of(this);
  ColorScheme get colors => Theme.of(this).colorScheme;
  TextTheme get text => Theme.of(this).textTheme;
  AppPalette get palette =>
      Theme.of(this).extension<AppPalette>() ?? AppPalette.light;
  bool get isDark => Theme.of(this).brightness == Brightness.dark;
}
