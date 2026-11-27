# Dashboard perso

Application personnelle qui centralise **budget**, **dépenses** et **tâches**. Elle tourne entièrement en local sur le Mac : un petit serveur web .NET et une base SQLite, sans cloud ni compte.

- **Stack** : .NET 10 (LTS), ASP.NET Core + Blazor (rendu interactif côté serveur), Entity Framework Core + SQLite.
- **Grilles** : `QuickGrid` (gratuit, fourni par Microsoft). Le code est organisé pour pouvoir passer à Telerik UI for Blazor plus tard (voir plus bas).

![Aperçu de l'accueil](docs/apercu-accueil.png)

## Lancer l'application sur macOS

### 1. Installer le SDK .NET 10 (une seule fois)

Au choix :

- Installateur officiel : https://dotnet.microsoft.com/download/dotnet/10.0 (prendre **macOS Arm64** pour un Mac Apple Silicon M1/M2/M3/M4, **x64** pour un Mac Intel) ;
- ou avec Homebrew : `brew install --cask dotnet-sdk`

Vérifier dans le Terminal :

```bash
dotnet --version   # doit afficher 10.0.x
```

### 2. Démarrer

```bash
cd chemin/vers/dashboard-perso/src/DashboardPerso
dotnet run
```

Le navigateur s'ouvre sur **http://localhost:5080**. Pour arrêter : `Ctrl + C` dans le Terminal.

Au premier lancement, la base est créée automatiquement avec cinq catégories de budget (Logement, Alimentation, Transport, Loisirs, Santé) à 0 €. Dans **Budget**, vous pouvez les renommer, les supprimer, ajouter des sous-catégories et saisir les montants **annuels**.

Pour le développement, `dotnet watch` recharge l'application à chaque modification de fichier.

## Où sont mes données ?

Dans un seul fichier SQLite :

```
~/Library/Application Support/DashboardPerso/dashboard.db
```

Le chemin exact est affiché dans le Terminal au démarrage (`Base de données : …`). Il est en dehors du dossier du code : supprimer ou recloner le projet ne touche pas aux données.

- **Sauvegarde** : copier ce fichier (application arrêtée), ou laisser Time Machine s'en charger.
- **Changer d'emplacement** (ex. un dossier iCloud Drive) : renseigner `Database:Path` dans `src/DashboardPerso/appsettings.json`.
- **Consulter la base** : n'importe quel outil SQLite (DB Browser for SQLite, `sqlite3`, l'extension SQLite de VS Code…).

## Ce que fait le squelette

| Page | Contenu |
|---|---|
| **Accueil** (`/`) | Dépensé ce mois-ci vs prévu, reste à dépenser, tâches ouvertes et en retard, budget par catégorie, prochaines tâches, dernières dépenses. |
| **Budget** (`/budget`) | Catégories et sous-catégories (ex. Automobile › Carburant, Assurance, Entretien), budget **annuel**, vue par mois (annuel ÷ 12) ou par année (avec le prévu à date). |
| **Dépenses** (`/depenses`) | Liste par mois, une vue par provenance (compte, chaque carte), filtre par catégorie, transferts affichables ou masqués, tri, pagination, total. Ajout, modification, suppression. |
| **Import** (`/depenses/import`) | Import du relevé CSV du Crédit Agricole : aperçu, catégories proposées par des règles, doublons écartés. |
| **Tâches** (`/taches`) | Filtres par statut, priorité, échéance (en rouge si dépassée), case à cocher pour terminer. Ajout, modification, suppression. |

## Catégories et budget

- **Budget annuel** : les montants saisis sont annuels. En vue « Mois », le prévu vaut budget annuel ÷ 12. En vue « Année », la colonne « Prévu à date » donne le budget au prorata des jours écoulés.
- **Sous-catégories** (un seul niveau) : lien **+ sous-catégorie** sur une catégorie principale. Le budget et les dépenses d'une catégorie principale cumulent ceux de ses sous-catégories. Une part « non répartie » est possible (budget ou dépenses affectés directement à la catégorie principale).
- **Dépenses et règles d'import** peuvent viser une catégorie principale ou une sous-catégorie. Filtrer les dépenses sur une catégorie principale inclut ses sous-catégories.
- **Supprimer une catégorie** : ses dépenses et ses règles peuvent être rattachées à une autre catégorie. Sinon, les dépenses restent sans catégorie et les règles sont supprimées. Une catégorie qui a des sous-catégories ne peut pas être supprimée tant qu'elle en a.

> Mise à jour depuis une version précédente : les anciens budgets mensuels sont automatiquement multipliés par 12 au premier lancement.

## Importer un relevé bancaire (Crédit Agricole)

1. Sur le site du Crédit Agricole, téléchargez la liste des opérations du compte au format **CSV**.
2. Dans l'application : **Dépenses → Importer un relevé**, puis choisissez le fichier.
3. Un aperçu s'affiche : vous pouvez décocher des lignes, corriger un libellé ou une catégorie. Rien n'est enregistré avant de cliquer sur **Importer**.

Ce qui est importé :

- **Les débits du compte** deviennent des dépenses. Les crédits (salaire, remboursements…) sont affichés mais pas importés.
- **Les paiements par carte** (cartes à débit différé, sections « carte » en fin de fichier) sont importés un par un, avec la carte d'origine (« Carte ••5524 · … »). Ils sont datés du jour du paiement.
- **Le débit mensuel de la carte** sur le compte est reconnu (libellé mentionnant la carte et montant égal au total de la section carte). Il est importé comme **transfert** : visible, sans catégorie, hors budget. Ainsi les paiements ne sont pas comptés deux fois.
- **Transferts** : une règle peut aussi marquer un libellé comme transfert, par exemple un virement vers un compte de crédit. On peut également cocher « Transfert » sur une dépense.
- **Doublons** : chaque opération importée garde une empreinte (date, montant, libellé complet). Réimporter un fichier qui chevauche le précédent n'ajoute que les nouvelles opérations. Si une dépense saisie à la main a la même date et le même montant, la ligne est marquée « doublon possible » et décochée.
- **Catégories** : la page **Règles de catégorie** associe un texte (ex. `ORANGE SA`) à une catégorie. Les majuscules et les accents ne comptent pas, et la règle la plus longue l'emporte. Le lien **+ règle** de l'aperçu crée une règle à partir d'une ligne et l'applique aussitôt aux autres.

Le libellé bancaire complet est conservé dans les notes de chaque dépense importée.

## Tests

```bash
dotnet test
```

Les tests utilisent un relevé **fictif** (`tests/DashboardPerso.Tests/Fixtures/ca-anonymise.csv`). Ne mettez jamais un vrai relevé dans le dépôt.

## Organisation du code

```
dashboard-perso/
├── DashboardPerso.slnx            Solution (ouvrable dans Rider, VS Code, Visual Studio)
├── dotnet-tools.json              Outil dotnet-ef pour les migrations
└── src/DashboardPerso/
    ├── Program.cs                 Démarrage : culture fr-FR, SQLite, migrations auto
    ├── Models/                    BudgetCategory, Expense, TaskItem
    ├── Data/AppDbContext.cs       Contexte EF Core
    ├── Data/Migrations/           Schéma de la base, versionné
    ├── Services/                  Accès aux données (BudgetService, ExpenseService, TaskService)
    ├── Services/Import/           Lecture du CSV bancaire, doublons, règles de catégorie
    ├── Components/Pages/          Accueil, Budget/, Depenses/, Taches/
    ├── Components/Shared/         Sélecteur de mois, barre de progression, pagination, bouton supprimer
    └── wwwroot/app.css            Styles (thème clair / sombre automatique selon macOS)
tests/DashboardPerso.Tests/        Tests xUnit (lecture CSV, doublons, règles)
```

Les pages ne parlent jamais directement à la base : elles appellent les services, qui renvoient de simples `List<T>`.

## Faire évoluer le modèle de données

Après avoir modifié une classe de `Models/` :

```bash
cd dashboard-perso
dotnet tool restore                                   # une seule fois
cd src/DashboardPerso
dotnet ef migrations add NomDeLaModification -o Data/Migrations
dotnet run                                            # la migration est appliquée au démarrage
```

## Passer à Telerik UI for Blazor (plus tard)

Les services renvoient des `List<T>`, ce qui correspond directement au paramètre `Data` du `TelerikGrid`. Pour migrer une page :

1. Ajouter le paquet `Telerik.UI.for.Blazor` (flux NuGet Telerik, licence requise) et suivre leur guide d'installation (`AddTelerikBlazor()`, `<TelerikRootComponent>` dans `MainLayout.razor`, CSS du thème).
2. Remplacer le `<QuickGrid Items="...">` de la page par un `<TelerikGrid Data="@items">` avec ses `<GridColumn>`. Les services et les modèles ne changent pas.

Chaque grille est signalée par un commentaire dans le code (`TelerikGrid`).
