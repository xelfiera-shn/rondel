# Rondel

**Yapay zekâ kodlama ajanları için dairesel başlatıcı · Claude Code ile çalışır**

[English](README.md) · Türkçe

<p align="center">
  <img src="docs/images/menu-tr.png" width="460" alt="Bir proje klasörü üzerinde Rondel'in dairesel menüsü; Oturum Seç dilimi son beş oturuma açılmış">
</p>

Windows masaüstünde veya Dosya Gezgini'nde **Ctrl + sol fare tuşunu** kısa bir süre basılı tutun: imlecin altında dairesel bir menü açılır. Bir dilim seçtiğinizde kodlama ajanınız, doğru klasörde açılmış bir terminalde başlar.

## Kurulum

1. [Claude Code](https://docs.anthropic.com/en/docs/claude-code)'u kurun ve terminalde `claude` komutunun çalıştığını görün.
2. [Son sürümden](https://github.com/xelfiera-shn/rondel/releases/latest) `Rondel-<sürüm>-win-x64.exe` dosyasını (ARM bilgisayarlar için `-win-arm64.exe`) indirin, istediğiniz bir klasöre koyup çalıştırın. Bildirim alanında Rondel ikonu belirir.
   - Dosya henüz kod imzalı olmadığı için ilk açılışta SmartScreen uyarısı çıkabilir: **Ek bilgi → Yine de çalıştır**. Her sürümde SHA-256 özetleri ve derleme kaynağı doğrulaması (build provenance) bulunur.
3. İkona sağ tıklayıp **Windows ile başlat**'ı işaretleyin.

Kurulum programı yoktur, yönetici izni gerekmez. Windows 10 ve 11 desteklenir.

## Kullanım

| Nerede basılı tutarsınız | Aksiyonlar | Klasör |
|---|---|---|
| Masaüstünde boş alan | Yeni Proje, Yeni + Prompt, Son Projeler | Proje klasörünüz (varsayılan `%USERPROFILE%\Projects`) ya da seçtiğiniz proje |
| Dosya Gezgini'nde bir klasörün boş alanı | Yeni Proje, Yeni + Prompt, Burada Aç, Burada Devam Et, Burada Oturum Seç | Açık olan klasör |
| Bir klasörün üzerinde (masaüstü veya Dosya Gezgini) | Aç, Devam Et, Oturum Seç | O klasör |

- **Aç** yeni bir oturum başlatır, **Devam Et** en son oturumu sürdürür, **Oturum Seç** listeden seçtirir (`claude`, `claude --continue`, `claude --resume`).
- **Oturum Seç dışa doğru açılır** (klasörde iki veya daha fazla oturum varsa): son beş oturum dilimin etrafındaki bir yayda belirir, en yenisi tam dilimin dışında. Birini seçerek o oturumu doğrudan açabilirsiniz.
- **Son Projeler** (masaüstü menüsü) en son çalıştığınız beş klasöre açılır. Birini seçince o projenin son oturumu açılır; iki veya daha fazla oturumu olan bir proje de aynı şekilde oturumlarına açılır.

<p align="center">
  <img src="docs/images/recents-tr.png" width="460" alt="Masaüstü menüsünde Son Projeler beş proje klasörüne, bir proje de oturumlarına açılmış">
</p>

- Devam Et ve Oturum Seç, son oturumun ne zaman olduğunu ve kaç oturum bulunduğunu gösterir; oturum yoksa soluk görünürler.
- **Yeni + Prompt** önce klasör adını (**Enter**), sonra prompt'u sorar (**Ctrl+Enter** başlatır, **Esc** geri döner).
- Dilime tıklayın, **1–N** tuşlarına basın ya da ok tuşları ve **Enter** kullanın; **Tab** seçili dilimin ya da projenin öğelerine (en yeniden başlayarak) geçer, **Esc** bir seviye geri çıkar ve en sonunda menüyü kapatır. Basılı tutmaya devam edip bir dilimin üzerine gelerek bırakmak da seçer.
- Normal Ctrl+tık ve Ctrl+sürükle her yerde eskisi gibi çalışır. Dosyalar, gezinti bölmesi, adres çubuğu, Bu Bilgisayar gibi sanal konumlar ve yönetici olarak çalışan Dosya Gezgini pencereleri menüyü açmaz.
- İsteğe bağlı bir klavye kısayolu (Ayarlar) menüyü fare imlecinin olduğu yerde açar.

## Ayarlar ve özelleştirme

Tray ikonu → **Ayarlar…**: dil (Otomatik / English / Türkçe), proje klasörü, basılı tutma süresi, klavye kısayolu, ajanın komutu (`claude` ya da `cc` gibi bir alias), terminal (Windows Terminal veya konsol penceresi) ve kabuk (PowerShell 7 veya Windows PowerShell).

Menüler, aksiyonlar ve ajanlar `%APPDATA%\Rondel\settings.json` içindedir; dosya kaydedilince değişiklik hemen uygulanır. Örneğin Claude Code'u plan modunda açan bir dilim eklemek için bir aksiyon ekleyip kimliğini bir menüye yazın:

```jsonc
"Actions": [
  // ... mevcut aksiyonlar ...
  { "Id": "plan", "Label": "Plan", "Icon": "E8FD", "Capability": "Open", "ExtraArgs": ["--permission-mode", "plan"] }
],
"Menus": {
  "FolderItem": ["open", "continue", "resume", "plan"]
}
```

`Label` düz metin olabilir; `@` ile başlayanlar yerleşik çevirilerdir. `Icon` bir [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) kod noktasıdır. Ajanlar, bir komut ve her yetenek (`Open`, `Continue`, `Resume`, `ResumeById`, `Prompt`) için argümanlardan oluşan profillerdir; başka bir kodlama ajanı da aynı şekilde eklenebilir — bkz. [docs/architecture.md](docs/architecture.md#agent-profiles).

## Sorun giderme

- Ayarlar'da **Hata ayıklama logu**'nu açın, sorunu tekrarlayın, sonra tray ikonu → **Log klasörünü aç** (`%LOCALAPPDATA%\Rondel\logs`). Her tetiklemede UI Automation zinciri, algılanan bağlam ve klasör yazılır.
- `Rondel.exe --probe X Y sonuc.txt` tek bir ekran noktasını (fiziksel piksel) tüm ayrıntılarıyla sınıflandırır.
- Terminal komutun bulunamadığını söylüyorsa Ayarlar'daki komutu ve `PATH`'i kontrol edin; PowerShell profilinizdeki alias'lar da çalışır.

[Hata bildirirken](https://github.com/xelfiera-shn/rondel/issues/new/choose) log satırlarını veya probe çıktısını eklemeniz çok yardımcı olur.

## Gizlilik

Rondel telemetri içermez. Ajanın yerel oturum verisini yalnızca ipuçlarını ve son oturumları göstermek için okur; kendi ayarları, logları ve (açarsanız) Windows ile başlatma kaydı dışında hiçbir şey yazmaz.

## Kaynaktan derleme

Windows ve .NET 10 SDK gerekir.

```powershell
dotnet build
dotnet test
.\build\publish.ps1     # dist\win-x64\Rondel.exe, tek dosyalık, bağımsız çalışan exe
```

Dallar, commit mesajları ve sürümler için [CONTRIBUTING.md](CONTRIBUTING.md), tasarım notları ve [yol haritası](docs/roadmap.md) için [docs/](docs/) klasörüne bakın.

## Lisans

[MIT](LICENSE) © Ahmet Şahin

---

Rondel bağımsız bir projedir; Anthropic ile bağlantılı değildir, Anthropic tarafından onaylanmamış veya desteklenmemiştir. Claude ve Claude Code, Anthropic, PBC'nin ticari markalarıdır.
