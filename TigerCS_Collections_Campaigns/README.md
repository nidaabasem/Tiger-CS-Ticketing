# Collections Campaigns

المرحلة الأولى جاهزة: شاشة معاينة قوائم التذكير والتصدير إلى CSV. لم تُنشر على UAT ولم تُربط بحملات Genesys الفعلية.

الحزمة مبنية على main عند commit `80ef8505585f5f21cce0fcb9bb9c33a788bc37e1` بعد PR #75.
تحتوي على patch و18 ملفًا مضافًا أو معدّلًا مع الحفاظ على مساراتها تحت files.

## تطبيق التعديل

من مجلد مشروع Tiger-CS-Ticketing، وعلى نسخة نظيفة من الكود:

```bash
git switch -c implementation/collections-campaigns
git apply --check /path/to/collections-campaigns.patch
git apply /path/to/collections-campaigns.patch
dotnet build src/TigerCS.slnx --configuration Release
dotnet test src/TigerCS.slnx --configuration Release --no-build
```

إذا فشل فحص patch بسبب اختلاف نسخة المشروع، لا تنسخي الملفات فوق نسخة مختلفة مباشرة. ادمجي التغييرات مع النسخة الحالية.

## تجربة الشاشة

1. شغّلي API وWeb بالإعدادات الحالية لقسم Collections واتصال PACTRPT.
2. افتحي Collections ثم Campaigns، أو `/Collections/Campaigns`.
3. اختاري المرحلة والتاريخ والشركة ثم Preview list.
4. بحساب CS Manager أو CS Supervisor أو موظف قسم Collections صاحب الصلاحية، اختاري Export review CSV.
5. ملف المراجعة داخلي، ويعرض الحالات المحتاجة مراجعة. لا ترفعيه كحملة تواصل فعلية.

اعتمدنا مواعيد الجدول: 1، 14، 28، والإشعار القانوني يومي 12 و14، والتحويل الداخلي يوم 30 أو آخر يوم في فبراير.
حدود 1,500 و20,000 درهم محسوبة لكل وحدة. التحويل إلى Legal قائمة مراجعة داخلية.

لا يوجد تعديل قاعدة بيانات أو Migration لهذه المرحلة. لا يتم إرسال أي رسالة أو إنشاء تذكرة أو قضية قانونية.

## قبل تفعيل ملف Genesys

تصدير Genesys يحتاج بيانات PACT تمت مطابقة مبالغها وتوزيعها على الوحدات، وتاريخ اليوم الموافق لموعد المرحلة، وكل الصفوف جاهزة.
الإعداد `Collections:Campaigns:FinancialSourceValidated` يبقى false حتى تنتهي المطابقة الفعلية.
الإشعارات القانونية تحتاج أيضًا `Collections:Campaigns:LegalNoticeExportEnabled`.
اقرئي `files/docs/Collections/Collections-Campaigns.md` للتفاصيل وقائمة اختبار UAT.

الإرسال الآلي والمزامنة وإيقاف العملاء بعد الدفع وإرجاع النتائج من Genesys تأتي في المرحلة التالية، بعد الاتفاق على قالب الأعمدة ومعرّفات الحملات.

## التحقق

- SDK 10.0.111، Release build: صفر أخطاء وصفر تحذيرات.
- جميع 3,201 اختبارات المشروع نجحت. استُخدمت إعدادات محلية لتعطيل مراقبة ملفات الإعدادات بسبب حد inotify في بيئة الفحص.
- اختبارات الحساب والصلاحيات والتصدير الكامل وإعادة قراءة الدفعات وواجهة Web ناجحة.
- لم يتم التحقق من بيانات PACT الحقيقية أو تشغيل حملة Genesys حقيقية.
