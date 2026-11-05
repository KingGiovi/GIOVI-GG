GIOVI.GG v0.3 — Windows Edition
================================
Novinky:
- Opraveno překrývání bočního menu (TableLayoutPanel)
- Export historie do CSV
- Smazání posledního zápasu (po potvrzení)
- Vylepšené ukládání tracker.xml
- Detekce LoL pouze podle běžící hry, ne launcheru

Tato verze je zdrojový projekt, nikoli hotový Windows EXE.
Pro vytvoření EXE bez Pythonu:
  1. Spusťte BUILD_AND_RUN.bat na Windows se .NET Framework 4.x.
  2. Nebo nahrajte GIOVI.GG.cs a workflow .github/workflows/build-windows.yml
     do GitHub repository, otevřete Actions > Build Windows EXE > Run workflow.
     Po dokončení sestavení stáhněte artifact GIOVI-GG-Windows-v0.3.

Data se ukládají do %LOCALAPPDATA%\GioviGG\tracker.xml a
nezmizí při změně složky nebo aktualizaci EXE.

Omezení: Aplikace zatím nesbírá statistiky zápasů automaticky.
GitHub workflow pouze vytvoří EXE, samo o sobě není auto-updater.
