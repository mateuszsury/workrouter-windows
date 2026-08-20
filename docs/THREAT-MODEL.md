# Model zagrożeń

## Zakres i aktywa

Chronione aktywa to: domowy LAN i jego usługi, katalog firmowy, konfiguracja WorkRouter, sekrety sesji/Wi‑Fi/SMB oraz prywatność metadanych ruchu. Ochrona obejmuje komputer z Windows i klientów podłączonych do segmentu WORK.

## Założenia

- Administrator komputera jest zaufany i kontroluje aktualizacje Windows.
- Ethernet jest uplinkiem do Internetu, a Wi‑Fi jest przeznaczone dla WORK.
- Firma może narzucać własne zasady VPN/EDR; WorkRouter ich nie obchodzi.
- Klient WORK może być częściowo niezaufany i może skanować dostępne adresy.

## Główne scenariusze

| Zagrożenie | Mitigacja | Ryzyko resztkowe |
|---|---|---|
| Klient WORK próbuje wejść do domowego LAN-u | Reguły WFP dla ruchu przekazywanego, testy negatywne IPv4/IPv6 | ICS i sterowniki mogą mieć różną obserwowalność; wymagany test fizyczny |
| Klient próbuje użyć usług hosta innych niż SMB | Domyślna blokada na interfejsie WORK, wyjątek tylko dla udziału | Nowa usługa może otworzyć port; monitoruj reguły i aktualizacje |
| Kradzież biletu panelu | Bilet krótkotrwały, API lokalne, chroniony stan | Pełna kompromitacja konta administratora obala te granice |
| Nieautoryzowany dostęp do katalogu | Osobne konto techniczne i ACL udziału | Błąd konfiguracji ACL lub kopia plików poza katalogiem |
| Klient obserwuje innego klienta WORK | Informacja o braku gwarantowanej izolacji peer-to-peer | Mobile Hotspot może umożliwiać komunikację między klientami |
| VPN/EDR zmienia routing | Jawna dokumentacja i ponowna walidacja po zestawieniu VPN | Polityka firmy może zablokować SMB lub Internet |
| Telemetria ujawnia domeny/IP | Ulotny pierścień metadanych, brak payloadu/MITM, przycisk czyszczenia | Metadane mogą być wrażliwe; ogranicz dostęp do hosta |
| Awaria watchdog/WFP | Fail-closed, zatrzymanie hotspotu przed odbudową oraz ponowny start dopiero po walidacji pełnej polityki | Awaria sterownika lub systemu może wykraczać poza kontrolę procesu; nieudane odzyskanie pozostawia stan `Faulted` |

## Poza zakresem

WorkRouter nie jest antywirusem, systemem DLP, pełnym IDS/IPS, proxy TLS, narzędziem do omijania polityki firmy ani gwarancją anonimowości. Nie chroni przed złośliwym administratorem, fizycznym dostępem do komputera ani kompromitacją samego Windows.

## Kryteria akceptacji

Przed użyciem służbowym wykonaj [weryfikację instalacji](INSTALLATION-VERIFICATION.md) na fizycznym sprzęcie. Każdy test niewykonany oznacz jako `NOT_RUN`; przejście testów jednostkowych nie zastępuje testu ICS/WFP na żywo.
