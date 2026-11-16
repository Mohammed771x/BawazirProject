# 12 — بناء الإصدار والنشر والنسخ الاحتياطي والاستعادة

طبّق `00-COMMON.md`. قيّم الإصدار المقصود: تجربة APK داخلية أم نشر متاجر أم خدمة عامة. لا تعامل متطلبات المتجر تلقائيًا كمانع تجربة داخلية، ولا تنفذ نشرًا أو rollback أو migration على الإنتاج ضمن التقييم.

**اقرأ:** Docker/entrypoint، csproj وrequirements وpubspec وpackage lock، `docs/09-DEPLOYMENT.md`، manifests وGradle وiOS configs، api_providers، migrations وDbContext، وفروق Git. راجع تعليمات البناء في `CLAUDE.md`؛ لا تشغل Flutter debug الذي يغير generated plugin registrant بالتزامن مع Android release.

1. ابنِ من نسخة نظيفة قابلة للإعادة أو checkout معزول، مع الحفاظ على diff المستخدم. سجّل hash المصدر وartifact وإصدارات dependencies وbuild number/config. تحقّق من `WORDOS_MOCK=false` وAPI HTTPS الصحيح ومن أن `VITE_ADMIN_MOCK` غير مفعّل في artifact المقصود. لا تضع secrets في defines أو bundle/image.
2. Android وiOS ضمن المنصات المقصودة: signing/entitlements/permissions/deep links/cleartext، تشغيل fresh install وترقية فوق نسخة سابقة مع حفظ session وتقدم server state. إعداد Android الحالي يسند release إلى debug signing؛ تحقق artifact والغرض، ثم قيّم أثره على المتجر والترقيات بدل إعلان عيب لكل توزيع داخلي.
3. migrations على قاعدة اختبار فارغة ثم على snapshot اصطناعي من إصدار سابق، بما فيها ProductDecisions/DecisionReviewSnapshot/AiConversations. runtime role بدون DDL، grants للجداول/sequence، ومراجعة عدم اعتماد بدء الخدمة على دور migrator. الموجود محليًا لا يعني مطبقًا على Neon.
4. compatibility: client قديم/API جديد والعكس عندما يسمح الدعم، deployment order لـoffset وAI schema والـadmin bundle، enum/حقول اختيارية. لا تحذف بيانات لتمرير upgrade. إن عكس migration غير آمن، وثّق rollback تطبيقي أو forward fix بدل DOWN على بيانات حقيقية.
5. Docker: non-root، Python loopback، startup readiness، shutdown SIGTERM، موت إحدى العمليتين، cold start وحجم الذاكرة المشترك. تحقق من headers/HTTPS خلف Render وتوجيه `/admin` وروابطها العميقة وstatic assets. صحة HTTP 200 وحدها لا تثبت صحة DB أو AI.
6. النسخ الاحتياطي: حدّد ما توفره **خطة Neon Free الحالية** من restore/history وسقف الاحتفاظ وما هو غير موجود، دون اختراع ضمان. حضّر backup محلي مشفر أو آلية معتمدة واختبر restore إلى قاعدة منعزلة باستخدام بيانات اصطناعية.
7. قِس RPO وRTO في تجربة استعادة: آخر write يمكن استرجاعها ووقت استعادة الخدمة، counts/checksums/قيود وعينات كاملة لدورة كلمة وtokens/قرارات/مراجعات. وجود ملف backup دون restore ليس نجاحًا. لا تنس lexicon edition والconfig الضرورية.
8. observability: القدرة على ربط طلب فاشل بخطأ دون أسرار، مقاييس حمل/AI/رفض، alerts للحصة وأثر cached readiness. غياب metric اقترح تحسينه بحسب أثره؛ لا توهم وجود مراقبة غير مفعلة.

أوامر البداية تُراجع حسب البيئة: `dotnet test` في backend، `python -m pytest` في ai-service، `flutter analyze` و`flutter test` في mobile، `npm run typecheck` و`npm run lint` و`npm run build` في admin-web. نفّذها حيث تتوفر، مع logs وأعداد skipped، ثم integration/device/build checks المناسبة. لا تشغّل أسرار أو خدمات خارجية اعتمادًا على أمثلة README دون التحقق من target.

التقرير: artifact manifest، توافق الإصدارات، قائمة readiness بنجاح/فشل/غير مقاس، تجربة restore وRPO/RTO، وخطة نشر/رجوع قابلة للمراجعة. لا تنشر النسخة ولا تنشئ release/commit نيابة عن المالك في هذه الجولة.
