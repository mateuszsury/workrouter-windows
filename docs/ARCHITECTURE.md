# Architektura WorkRouter for Windows

## Cel

WorkRouter uruchamia na jednym komputerze lokalny, odseparowany segment Wi‑Fi dla urządzeń firmowych. Ruch z tego segmentu jest przekazywany do Internetu przez Ethernet, a do hosta udostępniany jest wyłącznie jawnie skonfigurowany udział SMB. Domowa sieć LAN pozostaje poza zaufanym zakresem klientów WORK.

To narzędzie operatorskie dla Windows, a nie pełnoprawny firewall brzegowy ani system klasy enterprise. Skuteczność izolacji trzeba potwierdzić na konkretnym adapterze i wersji Windows.

## Komponenty

- `WorkRouter.Core` — modele konfiguracji, polityki, walidacja i logika niezależna od UI.
- `WorkRouter.Service` — usługa Windows: Mobile Hotspot/ICS, WFP, udział SMB, watchdog, API i lokalny panel.
- `WorkRouter.Launcher` — uruchamianie panelu po uzyskaniu krótkotrwałego biletu sesji.
- `tests/WorkRouter.Core.Tests` — testy jednostkowe oraz kontraktowe.
- `scripts/` — budowanie, instalacja, odinstalowanie i walidacja klienta.

## Sekwencja uruchomienia

1. Usługa odczytuje konfigurację i chroni sekrety mechanizmem DPAPI komputera.
2. Przygotowuje udział SMB oraz konto techniczne z ograniczonym ACL.
3. Wykrywa interfejsy Ethernet/Wi‑Fi i tworzy kandydacki profil sieci WORK.
4. Nakłada reguły WFP dla ruchu lokalnego i przekazywanego; domyślny stan jest fail‑closed.
5. Uruchamia Mobile Hotspot z Ethernetem jako uplinkiem.
6. Sprawdza bramę WORK, Internet, SMB i blokadę przykładowych celów domowego LAN-u.
7. Dopiero po pozytywnych bramkach usuwa kwarantannę klienta. Watchdog ponawia ochronę po utracie stanu.
8. Panel pokazuje stan, klientów, transfer oraz ograniczoną telemetrię przepływów.

Nieudane uruchomienie nie jest raportowane jako gotowe: usługa zatrzymuje częściowo uruchomione elementy i zachowuje bezpieczniejszy stan.

## Granice zaufania i przepływ

- Domowy router/LAN — sieć poza kontrolą WorkRouter; klienci WORK nie powinni jej osiągać.
- Host Windows — jednocześnie punkt wyjścia do Internetu i jedyny host udziału SMB.
- Segment WORK — urządzenia o różnym poziomie zaufania; widoczność między klientami zależy od Mobile Hotspot.
- Firmowy laptop — może mieć VPN, EDR lub lokalną politykę blokującą dostęp do sieci.
- Internet — zewnętrzny, niekontrolowany cel routingu.

API nasłuchuje lokalnie i wymaga biletu sesji/cookie. Launcher pobiera bilet, otwiera panel i przekazuje go tylko do lokalnej przeglądarki. API nie jest projektowane jako publiczny endpoint.

## WFP i ICS

WFP ma osobne reguły dla ruchu hosta i ruchu przekazywanego. Sama reguła aplikacyjna Windows Firewall nie jest wystarczającym dowodem izolacji ICS, dlatego walidacja klienta musi obejmować rzeczywiste próby połączeń do routera, NAS-a i usług domowych. IPv4 i IPv6 są sprawdzane oddzielnie.

Przechwytywanie telemetrii jest najlepszym wysiłkiem. Przekazywanie ICS, DoH, ECH, DoT, QUIC i VPN może ograniczyć obserwowalność. WorkRouter nie stosuje MITM i nie rejestruje payloadów.

## Udostępnianie plików

Udział SMB wskazuje tylko skonfigurowany katalog firmowy. Konto techniczne nie powinno mieć logowania interaktywnego ani praw do pozostałych danych. Ścieżka i nazwa udziału są wartościami konfiguracyjnymi; nie należy wpisywać sekretów do repozytorium.

## Ograniczenia

- Hotspot może nie izolować klientów WORK od siebie; funkcja pauzy/blokowania klienta jest dostępna tylko, gdy backend ją potwierdza.
- Zmiana konfiguracji pasma wykonuje kontrolowany restart routera i wymaga potwierdzenia obserwowanego pasma.
- Telemetria jest ulotna i metadanych, nie pełnym dziennikiem treści.
- Fizyczna akceptacja na firmowym laptopie, VPN/EDR i konkretnych adapterach pozostaje osobną bramką wydania.
