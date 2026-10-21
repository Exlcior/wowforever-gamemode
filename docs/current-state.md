# État courant — 21 octobre 2026

Le mount lit le client Forever local (`wowf`, 1.60.1.70245 sur cette machine), avec le
cache CDN déjà autorisé pour les fichiers absents du disque. Aucune donnée WoW convertie
n’est ajoutée à `Assets/`. Le démarrage reste direct, sans signature MiniBox.

## Jeu et personnages

- Démarrage en marche avec le `PlayerController` s&box et les collisions du terrain et
  des décors proches. Saut, accroupissement, première/troisième personne et vol libre.
  Le joueur attend le sol et les collisions proches avant de pouvoir se déplacer.
  Une courte attente laisse également au moteur le temps d'enregistrer les nouvelles
  formes physiques avant de chercher le point d'apparition.
- Personnage s&box avec sa tenue via `Dresser`, ou 20 personnages WoW : dix races, deux
  variantes, peau, visage et tenue de base, dix séquences natives de locomotion.
  Adaptateur et intégration dans un autre jeu : [characters.md](characters.md).
- Téléportation : l’ancienne caméra est libérée avant le changement de position.
  La marche commence sous l’explorateur, sans le décalage de la caméra troisième personne.
- Collisions WMO : lecture de `MOPY` et `MPY2` (villes reconstruites de Forever), indices
  `MOVI` et `MOVX`. Triangles réservés à la collision conservés ; détails non solides,
  groupes inaccessibles et antiportails exclus. Orgrimmar : 138 102 triangles de collision.

## PNJ, monstres, montures, sorts, équipement et zones — 21 octobre 2026

- 14 132 apparences animées (créatures, PNJ humanoïdes avec tenue du client, montures) ; 298/300 apparences
  tirées au hasard se lisent. PNJ et monstres aux positions Classic publiques (63 303, saisonniers exclus) à
  140 yards autour du joueur, au repos ou en promenade.
- Comportement serveur repris des données Classic publiques : 3 898 types de créatures ont des sorts (combat et
  hors combat), auras permanentes (ex. aura de l'esprit guérisseur), cavaliers sur leur monture (général de
  Hurlevent), poses (assis, endormi, travail, à genoux, mort) et armes en main (épée et bouclier des gardes).
  Viser une créature avec un sort déclenche sa riposte avec ses propres sorts pendant 20 secondes.
- Montures : menu F9, nom du client, cavalier sur le point de selle avec la posture de cavalier WoW, vitesse
  de monture. Le personnage s&box par défaut est posé sur la selle, sans posture assise.
- Sorts : 7 321 noms ; préparation aux mains, projectile (vol à la vitesse du client), impact, auras ; particules
  suivant les os animés et rubans (traînées). Les effets sont accrochés aux points d'attache du modèle
  (mains de sort, tête, torse) et suivent l'animation : contrôle sur les 20 personnages WoW et le Citizen, écart 0.
  Combat et dégâts : non.
- Le personnage WoW créé est l'avatar par défaut (humain au départ), gardé d'une session à l'autre ; course par
  défaut comme dans le client, Maj pour marcher. Interface aux polices du client (Friz Quadrata, Morpheus).
- Écran de création de WoW Forever, disposé d'après les fichiers d'interface du client (camelot) : colonnes
  Alliance/Horde avec bannières et icônes de race, types de corps, classes de la race, puis panneau de
  personnalisation par catégories ; pièces d'interface (atlas) et polices du client. Décor de chaque race avec sa
  caméra, son angle de vue, ses lumières, l'échelle et le recul prévus par le client (ChrModel). Vingt personnages
  habillés différemment et photographiés (`validation/2026-10-21-creatures/creation-*.png`). Limites : décor fixe
  (sans son animation d'ouverture), plan d'eau opaque dans le décor troll, pas de tenue de départ par classe (table
  absente de ce client).
- Bac à sable : objets du client sous `objets/<catégorie>/` et décors de création sous `glues/` dans le menu de spawn.
- Création de personnage (F9 → Personnage → « Créer mon personnage… ») : race et sexe, puis les options du client
  (couleur de peau, visage, coiffure, couleur des cheveux, pilosité, défenses...). Équipement et
  transmogrification : 6 559 objets portables, casques et épaulières adaptés à chaque race et sexe, pièces
  d'armure peintes sur la peau, géosets (manches, robes, bottes, gants, capes), armes et bouclier en main.
- État mort volontaire (F9 → Personnage → « Passer en état mort ») : il n'y a pas de vraie mort. Le monde passe en gris
  comme chez les fantômes du client, le personnage porte l'aura Fantôme du client (sort 8326), et les esprits
  guérisseurs, guides et créatures sous aura Fantôme ou Linceul de la mort n'apparaissent qu'à ce moment.
- Écrans de chargement du client par carte (Forever pour les continents) et bannière de zone/sous-zone.
- Preuves : `validation/2026-10-21-creatures/`.

## Distance et chargement

Relief détaillé jusqu’à 400 yards (neuf tuiles maximum), moyen à 1 600, horizon à 5 000.
Bâtiments à 2 600 plus leurs limites ; objets selon leur taille entre 300 et 1 800 ;
grands arbres/rochers dotés de LOD à 2 400. La petite végétation reste à 90 yards.

Préchargement au-delà de la portée, conservation des zones traversées, hystérésis des
LOD, apparition progressive et fondu entre versions. Un niveau absent ne crée pas de
LOD vide. Le budget de création de 4 ms est partagé entre types de ressources ; une
opération indivisible du moteur peut néanmoins le dépasser. L’arrivée masque la
construction initiale de la destination. Les composants récurrents sont gardés en
référence et les collisions manquantes sont réessayées avec un délai de 30 secondes.
Les listes de placements et les descriptions des grands bâtiments sont analysées
sur des threads de travail.

## Mount dans d’autres jeux

62 cartes / 3 038 tuiles, 7 510 modèles de placements, 62 094 modèles nommés et onze
lieux autonomes. Les modèles nommés proposent collisions et LOD natifs. Les scènes
utilisent le joueur du jeu hôte. Voir [mount.md](mount.md).

Les scènes autonomes sont des lieux fixes : petits objets à 220 yards, collisions de
décors à 650 (étendues aux bâtiments qui recouvrent ce rayon), terrain détaillé à 750,
grands décors à 2 200, bâtiments à 2 600 et horizon à 5 000. Pour parcourir un continent
avec chargement continu, utiliser l’explorateur et ses composants.

## Validation et performances

Preuves et scripts : `validation/2026-10-18-playable-mount/`. Compilation native et
contrôle local sans erreur. Les 20 personnages passent les contrôles de squelette,
poids, poses finies, mouvement des clips et atlas ; les 20 modèles ont été affichés
dans le moteur. Marche, saut et retour au sol vérifiés avec Citizen et WoW.
Les tests synthétiques couvrent les quatre combinaisons MOPY/MPY2 et MOVI/MOVX,
les triangles de collision absents du rendu et les groupes non solides.
Orgrimmar a été vérifiée dans l'explorateur et comme scène montée : arrivée au-dessus
du sol, marche, saut et retour au sol. Les erreurs de ressources restent à zéro
sur les parcours effectués à Northshire et Orgrimmar.

Dernier parcours hors éditeur : vol à 12 yards/s pendant 16 secondes, après chargement.
Sur les 512 dernières images : médiane 11,11 ms, P95 17,97 ms, P99 37,07 ms ; huit images
au-delà de 33 ms et une au-delà de 50 ms (maximum 50,34 ms). La fenêtre inactive est
plafonnée à 90 i/s. Mémoire vidéo : 3 681 Mio utilisés sur 5 046 Mio disponibles.

La fluidité parfaite n’est pas établie : plusieurs travaux partageaient le GPU et le
budget disponible variait. Le parcours de marche Citizen a subi des attentes de rendu
(P95 99,25 ms, maximum 193,41 ms ; avertissements GPU fence/present dans le journal).
Ce n’est pas une comparaison isolée avant/après. Le premier chargement et certaines
créations de collisions peuvent encore provoquer des pauses ; une création détaillée
de terrain a pris 68,73 ms pendant le vol. Les autres travaux ont été préservés.

## Limites conservées

- Pas de volumes physiques de nage ni de ragdolls WoW. Les objets à effets d'arme
  (enchantements lumineux) et les modèles de personnalisation additionnels ne sont pas encore reproduits. Le squelette WoW ne remplace pas automatiquement celui de Citizen :
  le jeu hôte doit intégrer l’adaptateur.
- Les autres M2 (créatures, moulins, drapeaux) gardent leur pose de repos ; leurs textures
  animées et particules existantes restent actives. Les couleurs vertex WMO et certaines
  variantes d’émetteurs ne sont pas encore reproduites.
- Dix-huit textures manquent au client lui-même dans l’audit antérieur (9 079/9 097).
  Les zones spécifiques à Forever sans lumière de référence gardent l’ambiance neutre.
- Les 62 cartes et 62 094 modèles n’ont pas tous fait l’objet d’un parcours visuel manuel.
  Pas de validation multijoueur entre comptes, ni de publication.

Historique : `validation/2026-10-17-mount/`, `2026-10-17-ambience/`,
`2026-10-17-effects/`, `2026-10-17-streaming/`.
