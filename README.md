# YNABfromFile

YNABfromFile to działająca w tle aplikacja .NET, która przegląda wskazane katalogi w poszukiwaniu wyciągów HTML, odczytuje z nich transakcje i wysyła je do wybranego budżetu oraz konta w YNAB przez API. Po udanym imporcie przenosi plik wejściowy i wygenerowane pliki pomocnicze do archiwum.

Aplikacja obsługuje wiele kont YNAB w jednym procesie. Nie pobiera plików z Google Drive: w typowym wdrożeniu robi to osobny skrypt `rclone`, a importer przegląda jego lokalne katalogi docelowe.

## Przepływ danych

```text
Plik HTML z wyciągiem
        |
        v
Katalog monitorowany z appsettings.json
        |
        | przegląd katalogu co 2 sekundy
        v
Odczyt tabeli i mapowanie wierszy na transakcje
        |                         |
        v                         v
Plik modified_*.csv       YNAB API: POST /budgets/{id}/transactions
        |                         |
        +------------+------------+
                     | udany import lub rozpoznane duplikaty
                     v
Katalog Archives/{data_czasu}/
  - oryginalny HTML
  - modified_*.csv
  - importids_*.csv (jeśli został utworzony)
```

Synchronizacja z Google Drive, jej harmonogram oraz usługa systemd są elementami wdrożenia serwera, a nie funkcjami implementowanymi przez kod tego repozytorium. Przykładowy przepływ produkcyjny opisany w materiałach wdrożeniowych to `rclone copy` do katalogów lokalnych, które następnie są monitorowane przez importer.

## Jak działa importer

1. Przy starcie ładuje `appsettings.json`, sprawdza obecność klucza API, identyfikatora budżetu oraz co najmniej jednej poprawnej konfiguracji konta.
2. Dla każdego konta tworzy katalog monitorowany i jego podkatalog `Archives`, jeśli ich brakuje.
3. Co 2 sekundy wyszukuje w katalogu głównym pliki `*.html`. Nie przeszukuje podkatalogów; pomija nazwy zaczynające się od `modified_`.
4. Po wykryciu nowego pliku odczekuje 500 ms, a następnie szuka w HTML wierszy tabeli pasujących do selektora `//table[@border='1']//tr[not(@class='head')]`.
5. Z każdego wiersza zawierającego co najmniej pięć komórek odczytuje datę, nazwę kontrahenta, kwotę i saldo. Jeśli tekst kontrahenta zawiera `DATA TRANSAKCJI:`, używa tej daty zamiast daty z pierwszej komórki. Nazwa kontrahenta jest obcinana przy pierwszym znaku `/`.
6. Tworzy `modified_{nazwa}.csv` z kolumnami `Date`, `Payee`, `Memo`, `Outflow`, `Inflow`, `Balance`. Separator CSV to średnik. Wypływ i wpływ są wyliczane ze znaku kwoty; pole `Memo` pozostaje puste.
7. Dla dat, które da się sparsować, tworzy transakcje YNAB. Kwoty są wysyłane w jednostkach milliunits (kwota pomnożona przez 1000), a stan transakcji ustawiany jest na `cleared`.
8. Wysyła transakcje do YNAB przez `POST {YnabApiUrl}/budgets/{YnabBudgetId}/transactions`, z nagłówkiem `Authorization: Bearer ...`.
9. Po odpowiedzi API tworzy `importids_{nazwa}.csv`, zawierający datę, kontrahenta, kwotę, `import_id` i identyfikator transakcji (jeśli jest dostępny).
10. Gdy API zwróci zaimportowane transakcje lub rozpoznane duplikaty, przenosi HTML, wynikowy CSV i dostępny plik identyfikatorów do `Archives/{yyyyMMdd_HHmmss}/`.

`import_id` jest deterministyczny między uruchomieniami: składa się z daty i skróconych skrótów kwoty, salda oraz nazwy kontrahenta. Dzięki temu API YNAB może rozpoznać ponownie przesłane transakcje, ale krótkie skróty mogą teoretycznie kolidować. To zabezpieczenie nie zastępuje kontrolowanego przełączenia między dwoma aktywnymi importerami.

### Zachowanie przy błędach

- Błędy odczytu lub parsowania pojedynczego wiersza są zapisywane w wyjściu procesu; pozostałe wiersze są dalej przetwarzane.
- Jeśli nie uda się wywołanie API, plik nie trafia do archiwum. Wygenerowany `modified_*.csv` pozostaje w katalogu monitorowanym.
- Wykryta ścieżka pliku jest zapamiętywana w pamięci procesu przed próbą importu. W razie błędu ten sam plik nie zostanie ponowiony automatycznie, dopóki proces nie zostanie uruchomiony ponownie. Po poprawieniu przyczyny błędu można zrestartować usługę, aby importer ponownie podjął plik.
- Lista już wykrytych plików istnieje tylko w pamięci i jest resetowana przy restarcie. Długoterminowy ślad udanych operacji stanowią katalogi `Archives` oraz identyfikatory transakcji po stronie YNAB.

## Konfiguracja

Plik `appsettings.json` powinien znajdować się obok uruchamianego programu. Aplikacja szuka go też w katalogu roboczym i maksymalnie dwa poziomy wyżej. Przykładowa struktura dla wielu kont:

```json
{
  "YnabApiKey": "WSTAW_TUTAJ_KLUCZ_API",
  "YnabBudgetId": "IDENTYFIKATOR_BUDZETU",
  "YnabApiUrl": "https://api.ynab.com/v1",
  "Accounts": [
    {
      "YnabAccountId": "IDENTYFIKATOR_KONTA_1",
      "MonitoringFolderPath": "/home/uzytkownik/YnabSync/Konto1"
    },
    {
      "YnabAccountId": "IDENTYFIKATOR_KONTA_2",
      "MonitoringFolderPath": "/home/uzytkownik/YnabSync/Konto2"
    }
  ]
}
```

`YnabApiUrl` jest opcjonalny; domyślnie używane jest `https://api.ynab.com/v1`. Nazwy właściwości JSON nie rozróżniają wielkości liter. Starsza konfiguracja pojedynczego konta, z polami głównymi `YnabAccountId` i `MonitoringFolderPath`, nadal jest obsługiwana.

Klucz API jest sekretem. Nie umieszczaj rzeczywistego klucza w repozytorium, zgłoszeniach ani dokumentacji. Ogranicz dostęp do pliku konfiguracyjnego, np. na Linuxie ustaw właściciela na użytkownika usługi i uprawnienia `600`. Pamiętaj też, że aplikacja zapisuje do standardowego wyjścia treść żądania transakcji i odpowiedzi API; logi mogą więc zawierać dane finansowe i powinny być odpowiednio chronione.

## Budowanie i uruchomienie

Wymagany jest .NET 8 SDK. Projekt korzysta z `HtmlAgilityPack` do odczytu HTML oraz `CsvHelper` do tworzenia CSV.

Uruchomienie z katalogu projektu:

```bash
dotnet run
```

Opublikowanie samodzielnego, pojedynczego pliku dla Linux x64:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish/linux-x64-single
```

Wynikiem jest `publish/linux-x64-single/YNABfromFile`. Skopiuj go razem z `appsettings.json` na serwer, nadaj programowi prawo wykonywania i uruchom go jako użytkownik mający dostęp do katalogów wejściowych.

```bash
chmod +x /ścieżka/YNABfromFile
/ścieżka/YNABfromFile
```

Proces działa ciągle i kończy pracę po `Ctrl+C`. W środowisku serwerowym zwykle jest uruchamiany przez menedżer usług.

## Uruchamianie jako usługa

Repozytorium zawiera przykładowe skrypty `setup_autostart.sh` i `service.sh`, które tworzą i kontrolują macOS LaunchAgent (`launchctl`). Nie są to skrypty instalujące usługę systemd na Linuxie. Dla Linuxa utwórz osobny unit systemd, uruchamiany jako użytkownik posiadający dostęp do konfiguracji i katalogów monitorowanych, albo zastosuj procedurę właściwą dla danego serwera.

W typowej instalacji na Linuxie warstwa synchronizacji może składać się z dwóch elementów niezależnych od importera:

- `rclone` kopiuje pliki z Google Drive do lokalnych katalogów. Przykładowe źródła i częstotliwość synchronizacji zależą od konfiguracji serwera.
- usługa systemd utrzymuje uruchomiony program `YNABfromFile` i przekazuje jego wyjście do dziennika systemowego.

Kolejność ma znaczenie przy migracji: przed uruchomieniem nowego importera produkcyjnego zatrzymaj stary, aby uniknąć równoległego wysyłania tych samych plików. Po migracji sprawdź logi, archiwum oraz wynik w YNAB.

## Ograniczenia i diagnostyka

- Importer oczekuje wyciągów HTML o strukturze zgodnej z selektorem tabeli opisanym wyżej. Nie obserwuje plików CSV jako danych wejściowych.
- Pliki wejściowe powinny mieć rozszerzenie `.html` i trafiać bezpośrednio do skonfigurowanego katalogu.
- Program nie pobiera ani nie autoryzuje Google Drive i nie zarządza `rclone`.
- Nie ma osobnej lokalnej bazy danych stanu importu. Nie kopiuj historycznych plików do aktywnego katalogu bez sprawdzenia, czy nie zostaną ponownie przetworzone.
- Aplikacja loguje błędy do standardowego wyjścia; sposób ich przechowywania zależy od tego, jak uruchomiono proces (np. dziennik systemd).
- W repozytorium nie ma projektu testowego. Po zmianie kodu sprawdź co najmniej kompilację i wykonaj kontrolowany test na przykładowym HTML, zanim włączysz importer produkcyjny.

Przy problemach sprawdź kolejno: czy konfiguracja jest dostępna i poprawna, czy konto usługi może odczytywać plik HTML, czy układ HTML zawiera oczekiwaną tabelę oraz jaki status i treść błędu zwraca YNAB API. Nie publikuj klucza API ani pełnych logów zawierających dane transakcji.

## Pliki projektu

- [`Program.cs`](Program.cs) — logika odczytu HTML, przygotowania transakcji, wywołania YNAB API i archiwizacji.
- [`YNABfromFile.csproj`](YNABfromFile.csproj) — cel .NET i zależności projektu.
- [`DEPLOY.md`](DEPLOY.md) — instrukcja publikacji i podstawowego wdrożenia.
- [`setup_autostart.sh`](setup_autostart.sh) i [`service.sh`](service.sh) — konfiguracja i kontrola macOS LaunchAgent.