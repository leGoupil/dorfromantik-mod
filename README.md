# Dorf Helper

Mod BepInEx pour Dorfromantik (macOS) qui suggère où placer les tuiles.

## Installation de BepInEx (déjà faite)

Dans `~/Library/Application Support/Steam/steamapps/common/Dorfromantik/` :

- BepInEx 5.4.23.5 (`BepInEx_macos_universal`) : `BepInEx/`, `libdoorstop.dylib`, `.doorstop_version`, `run_bepinex.sh`
- `run_bepinex.sh` modifié :
  - `executable_name="Dorfromantik.app"`
  - `dll_search_path_override="BepInEx/unstripped_corlib"` : le `mscorlib` du jeu est allégé par Unity,
    on fournit celui d'Unity 2021.3.45 (https://unity.bepinex.dev/corlibs/2021.3.45.zip)
  - `ARCHPREFERENCE="x86_64,arm64"` : Harmony/MonoMod de BepInEx 5 ne gère pas arm64, le jeu tourne sous Rosetta

Option de lancement Steam (Propriétés > Général > Options de lancement) :

    "/Users/lphilippe/Library/Application Support/Steam/steamapps/common/Dorfromantik/run_bepinex.sh" %command%

Désinstaller : vider l'option de lancement Steam (le jeu redevient vanilla), ou supprimer les fichiers listés ci-dessus.

## UnityExplorer

Inspecteur de scène en jeu (fork maintenu : https://github.com/yukieiji/UnityExplorer, v4.13.6, build BepInEx5.Mono),
installé dans `BepInEx/plugins/sinai-dev-UnityExplorer/`. Touche **F7** (fn+F7 sur Mac).

Le jeu livre des DLL Unity allégées (stripping) : UnityExplorer a besoin des versions complètes, placées dans
`BepInEx/unstripped_corlib/` (prioritaire grâce à `dll_search_path_override`) :
- `UnityEngine*.dll` : copiés depuis l'éditeur Unity 2021.3.45f1
  (`PlaybackEngines/MacStandaloneSupport/Variations/mono/Managed`)
- `UnityEngine.UI.dll` : recompilé depuis les sources uGUI de l'éditeur, voir `tools/ugui-build/`
  (`dotnet build -c Release`)
Config : `BepInEx/config/com.sinai.unityexplorer.cfg`.

## Développement

    cd src/DorfHelper && dotnet build -c Release

Le DLL est copié automatiquement dans `BepInEx/plugins/`. Logs : `.../Dorfromantik/BepInEx/LogOutput.log`.

## Décompilation

    DOTNET_ROLL_FORWARD=Major dotnet ilspycmd -p -o decompiled/Assembly-CSharp \
      -r "<Managed>" "<Managed>/Assembly-CSharp.dll"

(`decompiled/` est ignoré par git : c'est le code du jeu, à ne pas redistribuer.)

Classes clés :
- `TilePlacer` : tuile en main (`CurrentTile`), rotation, pose ; événement `OnNewPreviewTileSet`
- `TileSlotPreviewer.AllValidTileSlots` : cases où la tuile peut être posée
- `TileStack` : pioche (`Height`, `GetStackedTile(i)`)
- `World` : tuiles posées (`GetTile(gridPos)`, `GetAllPlacedTiles()`)
- `Tile.GetEdgeTypes(dir, Space)` : types de bord (`GroupTypeId` : Village, Forest, Agriculture, TrainTracks, Water ; liste vide = prairie)
- `QuestManager.AllQuestWatchers` / `QuestConditionWatcher` : quêtes, cible, valeur courante, statut
- `TileFitter` : règles d'emboîtement
- `AutoPlacer` : outil de debug des devs qui évalue chaque case × rotation (bords parfaits + simulation des quêtes)

## Snapshots

À chaque nouvelle tuile en main, le mod écrit l'état du jeu dans
`.../Dorfromantik/BepInEx/DorfHelper/snapshots.jsonl`.

## Solveur (v0.4)

- `Model/Board.cs` : modèle pur du plateau (cases, segments, groupes en union-find, bords ouverts, quêtes).
  Reproduit les règles du jeu : un bord de segment est ouvert tant qu'aucune tuile ne le touche ; deux segments
  du même type face à face fusionnent ; une quête de comptage échoue si son groupe se ferme trop tôt
  ou si une quête « exactement » est dépassée.
- `Model/Snapshotter.cs` : construit le modèle depuis la partie (sans la tuile d'aperçu, qui fusionne les groupes).
- Tuiles gagnées (règles de `RewardSystem`) : placement parfait (6 côtés occupés, 6 bords qui collent) = +2 tuiles,
  y compris pour une voisine dont la pose complète le 6ᵉ côté ; quête réussie = `questRewardByScore(score)` tuiles.
  Les modes « durer » maximisent ces tuiles gagnées ; le log « Vérif tuiles gagnées » compare prédiction et réalité.
- Tuiles probables : tirées parmi les 80 dernières tuiles posées (le jeu ne tire le terrain d'une tuile que quand
  elle devient visible, seules les 2 suivantes sont connues). Plusieurs tirages, communs à tous les candidats,
  moyennés ; leur nombre s'adapte à un budget d'environ 1,5 s.
- `Solver.cs` : note chaque pose (bords + effet exact sur les quêtes), puis pour les 10 meilleures,
  joue les tuiles suivantes de la pioche « au mieux » (glouton, actualisation 0.85). Tourne en arrière-plan.
- Auto-vérification (log « Vérif pose » / « Vérif modèle ») : juste avant chaque pose (crochet Harmony sur
  `TilePlacer.PlaceCurrentTile`) et à chaque survol, le modèle est comparé au jeu (bords et valeur des quêtes).
- Quêtes de la tuile en main : modélisées via `QuestModel.HandSegs`, elles deviennent des quêtes normales une fois posées.
- Limite connue : les tuiles adaptatives (qui changent de forme selon leurs voisins) sont vues avec leur forme au moment de la photo.

| Touche (fn+ sur Mac) | Action |
|---|---|
| Ctrl+Cmd (maintenus) | Comme F5, verrouillé tant qu'on tient ; clic = poser ; rappui = suggestion suivante ; en gardant les touches, la suggestion suivante s'affiche après chaque pose |
| F5 | Place l'aperçu sur la suggestion suivante (1 → 2 → 3) avec la bonne rotation, centre la caméra et verrouille l'aperçu (clic = poser, bouger la souris = déverrouiller) |
| F6 | Pose la tuile sur la suggestion affichée |
| F4 | Anticipation : aucune / 2 visibles (défaut) / 2 visibles + 5 probables / 2 visibles + 10 probables |
| F8 | Objectif : score / quêtes / durer / score sans bord raté / durer sans bord raté |
| F9 | Masque/affiche l'overlay |
| F10 | Mode auto : pose la meilleure suggestion à chaque tuile (pause pendant les menus/récompenses) |
| F2 | Relance automatique on/off : en mode auto, nouvelle partie (même mode de jeu) 8 s après une fin de partie tant que le défi choisi n'est pas réussi |
| F3 | Objectif de biome : aucun → défis « grand groupe » du jeu non accomplis → objectif perso (config) → aucun |

Le mod est compilé contre les DLL Unity complètes de `BepInEx/unstripped_corlib` (IMGUI pour l'overlay).

## Objectif de biome (v0.12)

F3 propose les défis du jeu (quêtes de session `CloseBigGroup` / `FormBigGroup` non accomplies, lus via
`SessionQuestWatcher`) avec leurs règles exactes (fermer ou former, éléments ou segments), puis l'objectif perso.

Ex. « fermer un champ de 150 champs » (config `[Objectif de biome]` : `Terrain = Agriculture`, `Element = Field`, `Cible = 150`).
- Cible : le groupe ouvert du terrain avec le meilleur compromis taille × perspectives, conservé tant qu'il est ouvert
  (sauf s'il est piégé et qu'un autre est nettement plus prometteur).
- Phase 1 (croissance) : bonus par élément ajouté, pénalité par bord ouvert perdu sans croissance, pénalité forte si
  le groupe est fermé avant la cible, prise en compte des perspectives (part des tuiles capables de prolonger chaque bord).
- Phase 2 (fermeture) : bonus par bord refermé, gros bonus quand le groupe est fermé.
- Dosage par la réserve : bonus coupés sous 15 tuiles en pioche, plein régime à partir de 40 (survie d'abord) ;
  les protections du groupe (ne pas le fermer, ne pas lui retirer de bords) restent toujours actives.
- Croissance normalisée par segment (moyenne d'éléments par segment du terrain) : une tuile de forêt porte beaucoup
  d'arbres, sans normalisation l'objectif forêt écrasait la survie.
- Défis « plusieurs groupes » (`CloseManyGroups` / `FormManyGroups`, ex. 50 champs fermés d'au moins 10 champs,
  progression cumulée entre parties) : prime par groupe fermé assez grand, croissance plafonnée à la taille cible
  (fusionner deux groupes déjà assez grands est pénalisé), bonus de fermeture une fois la taille atteinte.
  Simulateur : `--many Agriculture:Field:10` → ~2,4 groupes fermés pour 100 tuiles (contre 0,5 sans le mode).

## Simulateur (`tools/sim`)

Rejoue des parties avec le modèle et le solveur du mod, sur les tuiles et quêtes enregistrées en jeu
(`BepInEx/DorfHelper/tiles.jsonl`, `quests.jsonl`).

    cd tools/sim && dotnet run -c Release -- --games 30 --objective Compact,CompactClean --quest-scale 0.4
    dotnet run -c Release -- --games 20 --objective Compact --goal Agriculture:Field:150
    dotnet run -c Release -- --games 20 --objective Compact --goal TrainTracks::80        # segments
    dotnet run -c Release -- --games 20 --objective Compact --goal Water::100:form       # former sans fermer

Options : `--depth`, `--stack`, `--max`, `--seed`, `--quest-scale` (calibrage de la difficulté des quêtes),
`--goal-grow/-loss/-prospect/-close/-min-stack/-full-stack`, `--verbose 1`.

## Défis « d'un coup » (v0.13)

- D — `Challenge_PerfectPlacementsAtOnce` (ex. 7 placements parfaits d'un coup) : il faut une « fleur », un trou dont
  les 6 voisines sont complètes sauf lui avec tous leurs bords qui collent, puis une tuile qui colle sur ses 6 bords.
  Planification (v0.14) : choix d'un site (centre C, anneau intérieur 6, anneau extérieur 12) maximisant
  avancement × √(chance de remplir chaque position manquante × chance de trouver la tuile du centre) ; bonus par
  progression, forte pénalité si une pose gâche le site (raccord raté, centre occupé trop tôt). Simulateur :
  ~1 partie sur 3 réussit les 7 placements parfaits d'un coup en 500 tuiles (contre 0 sans planification).
- E — `Challenge_ScorePerPlacement` (ex. 660 points en une pose). Points d'une pose (`PlacementEval.Points`) :
  raccords (2/bord), placements parfaits (60 chacun), fermeture de groupes (3 × (segments − 1)^1,2), quêtes (100).
  Stratégie : faire grossir le plus grand groupe ouvert jusqu'à la taille dont la fermeture rapporte la cible
  (~91 segments pour 660), puis le fermer. Le log « Vérif points » compare prédiction et réalité.
- Simulateur : `--atonce perfect:7`, `--atonce points:660`.
