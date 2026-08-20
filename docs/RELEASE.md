# Proces wydań

## Źródła prawdy

Wydanie składa się ze źródła repozytorium, pozytywnego CI, powtarzalnego builda i manifestu hashy. Nie publikujemy binariów z nieznanego katalogu ani z lokalnymi sekretami.

## Build

Na Windows uruchom:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Skrypt uruchamia testy Release, publikuje usługę self-contained `win-x64`, publikuje launcher jako self-contained single-file i tworzy `artifacts\publish\SHA256SUMS.txt`. Przed wydaniem sprawdź zawartość manifestu oraz hash każdego pliku.

## Bramka CI

CI wykonuje restore/build/test rozwiązania .NET, `node --check` dla panelu i walidację składni PowerShell. Artefakty CI są dowodem technicznym, nie dowodem akceptacji fizycznego ICS/WFP.

## Checklist publikacji

- [ ] Zmiana ma wpis w `CHANGELOG.md` i numer SemVer.
- [ ] Build wykonano z czystego drzewa; testy i parsery zakończyły się powodzeniem.
- [ ] Manifest SHA-256 odpowiada dokładnie paczce.
- [ ] Pakiet nie zawiera haseł, tokenów, PII ani zrzutów z realnego środowiska.
- [ ] Instalacja, rollback i odinstalowanie sprawdzone na maszynie testowej.
- [ ] Windows 11, różne adaptery Wi‑Fi, ICS, WFP, SMB, IPv6 oraz VPN/EDR mają osobne wyniki; nieznane bramki są `NOT_RUN`.
- [ ] Binariów nie opisano jako podpisanych, jeśli nie ma ważnego Authenticode.
- [ ] Tag jest podpisany i przechodzi `git verify-tag` z `.github/allowed_signers`; status `Verified` na GitHubie jest dodatkową kontrolą konta.
- [ ] Release zawiera ZIP, dwa manifesty SHA-256 oraz SPDX SBOM.
- [ ] `gh attestation verify <ZIP> --repo mateuszsury/workrouter-windows` kończy się powodzeniem.

Publiczne artefakty są publikowane na stronie [GitHub Releases](https://github.com/mateuszsury/workrouter-windows/releases) dopiero po przejściu powyższych bramek.

## Granica bezpieczeństwa

Podpis taga, manifesty, SBOM i attestacje potwierdzają pochodzenie oraz integralność artefaktu; nie są certyfikatem bezpieczeństwa aplikacji ani gwarancją braku podatności. Informacje o podatnościach prowadzi się prywatnie według [SECURITY.md](../SECURITY.md), a nie w opisie release. Jeśli którakolwiek bramka nie została wykonana, wydanie musi pozostać oznaczone jako niezweryfikowane lub `NOT_RUN`.
