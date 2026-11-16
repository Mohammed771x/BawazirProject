# 11 — لوحة الإدارة وصحة الأرقام والقرارات

طبّق `00-COMMON.md`. اختبر أن التقرير يقول ما تقيسه البيانات فعلًا؛ غياب البيانات ليس صفر أداء، والمقارنة ليست إثبات سببية. لا تطلب تغيير تصميم الإدارة لمجرد احتمالات مستقبلية.

**اقرأ:** `admin-web/src/`، `backend/src/WordOs.Api/Admin/` خصوصًا IntelMeasures/IntelMetrics/IntelDataset/IntelEvaluation/IntelNotifications/AgentTools، EventEndpoints وanalytics tracker، `docs/10-ADMIN-INTELLIGENCE-AUDIT.md` وADR-125/132/133/134. استخدم AdminIntelTests/AdminProductTests/AnalyticsTests/AdminAgentTests، وأضف E2E للويب إن لم يوجد.

- أنشئ dataset صغيرة يمكن حسابها يدويًا: 3 متعلمين مع Owner وAnalyst، كلمات نشطة/محذوفة/مؤرشفة، جلسات ناجحة وفاشلة ومُعادة وممتدة لليوم التالي، مراجعة صحيحة أول مرة وأخرى بعد retry، أحداث قرب منتصف الليل، إشعارات مخططة ومفتوحة، عينة بلا أحداث عميل. احسب expected من fixture مستقلة عن دالة الإنتاج.
- طابق overview/funnel/lifecycle/pass rates/retention D1/D7/D30/skills/content version/AI fallback/typical duration. تحقق من numerator/denominator، استبعاد حسابات الإدارة حيث يلزم، الفرق بين sessions/word outcomes/attempts، وعدم عد retry كنجاح أول محاولة. افحص zero denominator وnull وsmall n وحدود التقرير الزمنية +3 الافتراضية مقابل timezone للجهاز.
- الفلاتر في URL، previous period/cohort/segment/skill/level/date، pagination/search/day detail/timeline، empty/error/loading، back/forward، لغة RTL ورسوم مع وحدات وlegend. المقاييس تُحسب بالخادم لا بمعادلة مختلفة في المتصفح.
- استقبال events: allowlist، ملكية resource IDs، timestamps مستقبلية/قديمة، قيم كبيرة، batch جزئي وتكرار الإرسال، background flush وتبديل الحساب. اختبر عدم تسجيل أسرار أو محتوى غير لازم. صف deduplication الفعلية، ولا تفترض exactly-once في شبكة متقطعة.
- الإشعارات: planned ≠ delivered ≠ opened. حدّد مدى إثبات ربط الفتح بالدرس. نقص reminder kind موثق في audit؛ تحقق هل ما زال موجودًا ووسم attribution المستنتج بوضوح. لا تسجل استنتاجًا كحدث وصول مؤكد.
- feedback/notes/status/audit: النص محفوظ وعرضه آمن، إعادة فتح handled، اختلاف صلاحيات User/Analyst/Owner. أعد فحص الصلاحية عبر API المباشر.
- القرار: proposal→approval→implementation→review→close وفق state machine الفعلية؛ action غير مسموح، review due، تكرار request، metric غير معروف، حفظ baseline/evaluation snapshot عند الإغلاق، audit history وصلاحيات الاعتماد. لا تقبل أن AI يعتمد قرارًا بدل Owner.
- changes/experiments: نوافذ before/after، cohort control، فرق الفروق عندما ينطبق، التغييرات المتداخلة، أحجام العينات والتغير من baseline صفر. تأكد أن الواجهة لا تقدم علاقة زمنية كبرهان سببي.
- محادثة AI ثم تحويل الرد إلى investigation/proposal، مصادر الأرقام، cache/filters، عزل المحادثات. جودة النموذج في 05؛ هنا صحة ربط البيانات والواجهة.

التسليم: جدول metric/definition/source/expected/actual/result، اختبارات backend وbrowser، screenshots ممثلة، ودليل أن الرقم المعروض يطابق المخزن. ملاحظات scale تذهب إلى 09، ولا تصف computation in-memory عيبًا إذا نجح ضمن حجم الهدف.
