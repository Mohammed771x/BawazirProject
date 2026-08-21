import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/theme/app_tokens.dart';

/// One country's dialling information.
class CallingCode {
  const CallingCode({
    required this.iso,
    required this.dialCode,
    required this.flag,
    required this.nameEn,
    required this.nameAr,
  });

  final String iso;
  final String dialCode;
  final String flag;
  final String nameEn;
  final String nameAr;

  String label(bool arabic) => arabic ? nameAr : nameEn;
}

/// The countries WordOS learners actually come from, most likely first.
///
/// A short curated list rather than all 200: a picker the learner scrolls
/// through for a minute is worse than one that shows their country third. It is
/// data, not logic, so extending it costs nothing.
const kCallingCodes = <CallingCode>[
  CallingCode(iso: 'YE', dialCode: '967', flag: '🇾🇪', nameEn: 'Yemen', nameAr: 'اليمن'),
  CallingCode(iso: 'SA', dialCode: '966', flag: '🇸🇦', nameEn: 'Saudi Arabia', nameAr: 'السعودية'),
  CallingCode(iso: 'AE', dialCode: '971', flag: '🇦🇪', nameEn: 'United Arab Emirates', nameAr: 'الإمارات'),
  CallingCode(iso: 'EG', dialCode: '20', flag: '🇪🇬', nameEn: 'Egypt', nameAr: 'مصر'),
  CallingCode(iso: 'JO', dialCode: '962', flag: '🇯🇴', nameEn: 'Jordan', nameAr: 'الأردن'),
  CallingCode(iso: 'OM', dialCode: '968', flag: '🇴🇲', nameEn: 'Oman', nameAr: 'عُمان'),
  CallingCode(iso: 'QA', dialCode: '974', flag: '🇶🇦', nameEn: 'Qatar', nameAr: 'قطر'),
  CallingCode(iso: 'KW', dialCode: '965', flag: '🇰🇼', nameEn: 'Kuwait', nameAr: 'الكويت'),
  CallingCode(iso: 'BH', dialCode: '973', flag: '🇧🇭', nameEn: 'Bahrain', nameAr: 'البحرين'),
  CallingCode(iso: 'IN', dialCode: '91', flag: '🇮🇳', nameEn: 'India', nameAr: 'الهند'),
  CallingCode(iso: 'PK', dialCode: '92', flag: '🇵🇰', nameEn: 'Pakistan', nameAr: 'باكستان'),
  CallingCode(iso: 'TR', dialCode: '90', flag: '🇹🇷', nameEn: 'Türkiye', nameAr: 'تركيا'),
  CallingCode(iso: 'MY', dialCode: '60', flag: '🇲🇾', nameEn: 'Malaysia', nameAr: 'ماليزيا'),
  CallingCode(iso: 'GB', dialCode: '44', flag: '🇬🇧', nameEn: 'United Kingdom', nameAr: 'المملكة المتحدة'),
  CallingCode(iso: 'US', dialCode: '1', flag: '🇺🇸', nameEn: 'United States', nameAr: 'الولايات المتحدة'),
];

/// A phone field with a country-code selector.
///
/// The code and the number are kept as two values all the way to the database,
/// because they answer different questions and splitting a concatenated string
/// afterwards means guessing where the prefix ends (+1 vs +1-242).
///
/// The field itself is pinned to LTR: a phone number is read left-to-right in
/// every language, and an Arabic layout would otherwise put the digits in an
/// order that does not match what the learner typed.
class CountryCodeField extends StatelessWidget {
  const CountryCodeField({
    super.key,
    required this.selected,
    required this.onCountryChanged,
    required this.controller,
    required this.label,
    required this.arabic,
    this.validator,
  });

  /// Refuses the form when the number is missing (ADR-054). Passed in rather
  /// than built here so the wording stays with the screen's own strings.
  final String? Function(String?)? validator;

  final CallingCode selected;
  final ValueChanged<CallingCode> onCountryChanged;
  final TextEditingController controller;
  final String label;
  final bool arabic;

  @override
  Widget build(BuildContext context) {
    return Directionality(
      textDirection: TextDirection.ltr,
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 132,
            child: DropdownButtonFormField<CallingCode>(
              initialValue: selected,
              isExpanded: true,
              decoration: const InputDecoration(border: OutlineInputBorder()),
              items: [
                for (final country in kCallingCodes)
                  DropdownMenuItem(
                    value: country,
                    child: Text(
                      '${country.flag}  +${country.dialCode}',
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
              ],
              selectedItemBuilder: (context) => [
                for (final country in kCallingCodes)
                  Align(
                    alignment: Alignment.centerLeft,
                    child: Text('${country.flag}  +${country.dialCode}'),
                  ),
              ],
              onChanged: (country) {
                if (country != null) onCountryChanged(country);
              },
            ),
          ),
          const SizedBox(width: AppSpacing.xs),
          Expanded(
            child: TextFormField(
              controller: controller,
              keyboardType: TextInputType.phone,
              // Digits and the separators people naturally type. The server
              // strips everything but digits before storing.
              inputFormatters: [
                FilteringTextInputFormatter.allow(RegExp(r'[0-9 ()\-]')),
                LengthLimitingTextInputFormatter(20),
              ],
              validator: validator,
              decoration: InputDecoration(
                labelText: label,
                border: const OutlineInputBorder(),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
