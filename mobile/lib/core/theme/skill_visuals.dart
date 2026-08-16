import 'package:flutter/material.dart';

import '../models/enums.dart';
import 'app_tokens.dart';

/// The visual identity of each skill — one place, used everywhere.
class SkillVisuals {
  const SkillVisuals._();

  static Color color(BuildContext context, SkillType skill) {
    final p = context.palette;
    return switch (skill) {
      SkillType.reading => p.skillReading,
      SkillType.listening => p.skillListening,
      SkillType.speaking => p.skillSpeaking,
      SkillType.writing => p.skillWriting,
      SkillType.spelling => p.skillSpelling,
    };
  }

  static IconData icon(SkillType skill) => switch (skill) {
        SkillType.reading => Icons.menu_book_rounded,
        SkillType.listening => Icons.headphones_rounded,
        SkillType.speaking => Icons.record_voice_over_rounded,
        SkillType.writing => Icons.edit_note_rounded,
        SkillType.spelling => Icons.spellcheck_rounded,
      };
}
