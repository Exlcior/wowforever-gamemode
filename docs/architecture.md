# Architecture

Le projet a été créé par le template moteur `game.minimal`, puis configuré par le socle
MiniBox. La scène de démarrage est `scenes/exploration.scene` : soleil, ciel, caméra,
streaming du monde, vol libre et destinations. La signature de lancement a été retirée à la
demande d'Exl ; aucune synchronisation de signature ne doit la réinstaller.

## Données : le mount `wowforever`

Toutes les données viennent de l'installation du joueur par le mount
(`source/mount/wowforever/`, détails dans [mount.md](mount.md)). Il expose des ressources
`mount://wowforever/...` : catalogue, terrains à trois niveaux, placements, modèles et leurs
descriptions, textures de particules et ambiances. Lecture et décodage se font sur des
threads de travail ; le fil principal ne crée que les objets natifs, dans un budget de 4 ms
par image découpé en étapes. Les descriptions JSON passent par la métadonnée `WowData`
d'un prefab. L'unité source (yard) vaut 36 pouces s&box ; les modèles suivent la convention
du client convertie en (-y, x, z).

`Code/MountLoads.cs` partage les chargements en cours : redemander une ressource du mount
pendant son chargement bloquerait le fil principal.

## Chargement

`AzerothWorld` charge le terrain en trois anneaux mesurés au bord des tuiles, en yards :
détail complet jusqu'à 400 (au plus `TileBudget` tuiles, couches MCAL d'origine), relief
moyen jusqu'à 1 600 (un sommet sur 4) puis horizon jusqu'à 5 000 (un sur 16), avec la
texture de carte du client et les liquides. Le niveau précédent reste affiché jusqu'à
l'arrivée du suivant ; une hystérésis évite les bascules répétées. À chaque arrivée, un
préchargement à budget élevé charge tous les anneaux et les décors, puis seul un flux
léger continue. Un brouillard accordé à l'horizon du ciel termine l'anneau lointain.
L'origine est recentrée au-delà de 2 000 yards.

`AzerothDetails` déduplique les placements, partage les modèles et charge les décors du plus
proche au plus lointain : bâtiments jusqu'à 2 600 yards plus leur rayon, objets M2 de 300 à
1 800 selon leur taille, végétation jusqu'à 90. Les décors internes des WMO suivent le repère
du bâtiment et l'ensemble de décors choisi par le placement. Les parties d'un modèle qui
partagent un matériau forment un seul appel de dessin. Les collisions précises sont préparées dans un rayon de 180 yards plus les limites du modèle ; le joueur est immobilisé si une collision à moins de 30 yards manque encore.
HLOD : au-delà de 160 yards (plus selon la taille), une copie prend la version réduite que livre le client
(`models/<id>_lod1/_lod2`), et les grands objets qui en ont une (arbres, rochers) restent visibles jusqu'à 2 400
yards dans leur version la plus légère. Une bande de préchargement prépare terrain et modèles juste au-delà des
anneaux, et les zones traversées restent en mémoire (bornée) pour les demi-tours.

Les décors apparaissent progressivement sur les derniers 12 % de leur portée (18 à 250 yards),
et les changements de LOD se fondent sur environ un tiers de seconde. Une hystérésis de
15 % et une marge de conservation de 120 yards évitent les allers-retours à la frontière.
Le préchargement garde un worker même lorsque le chargement visible est occupé.
Terrain, modèles et personnages partagent le même budget de création, non générique.

`AzerothPlayer` crée un `PlayerController` natif lorsque le sol et les collisions sont prêts.
La tenue du joueur s&box passe par `Dresser`. `WowCharacterAnimator` sélectionne les séquences
WoW selon le déplacement ; le contrôle natif Citizen reste actif pour le modèle s&box.
Les composants courants sont gardés en référence : pas de recherche de scène à chaque image.

## Matériaux, animations et effets

Les shaders ci-dessous appartiennent à la mount (`source/mount/wowforever/Assets/shaders/wowforever/`) et sont
installés à côté de sa DLL.

- Terrain : `terrain.shader`, jusqu'à huit couches et leurs masques MCAL, échelle MTXP et
  mélange de hauteur quand il existe.
- Props : `complex.shader` avec le mode de mélange d'origine (découpe, transparence,
  addition) ; `prop_blend.shader` pour multiplication et alpha prémultiplié ;
  `prop_anim.shader` pour les textures animées (déplacement des UV des M2 : cascades, lave,
  halos) et les surfaces non éclairées (flammes, lanternes, vitraux).
- Liquides : `liquid.shader`, couleurs et textures de LiquidType ; eau qui lit le fond,
  ondule et mousse sur les rives ; lave et gangrène émissives et animées.
- Particules : `AzerothParticles` crée, à moins de 160 yards, les émetteurs M2 d'origine
  (feux, fumées, magie) en effets de particules s&box : vitesse, dispersion, gravité, durée,
  débit, zone, couleurs, opacité et taille sur la vie, planches de sprites découpées.
- Ambiance : `AzerothAmbience` mélange les lumières de zone (soleil, ambiance, brouillard,
  ciel en bandes `sky.shader`).

## Outils

`tools/install_wowforever_mount.py` compile et installe le mount. `tools/mount_data/`
régénère les tables compilées dans le module (`generate_mount_tables.py`) et le catalogue
d'ambiances (`prepare_ambience.py`). Aucun service permanent, aucun port HTTP ni
publication. Les anciens outils de conversion vers Assets sont dans `archive/`.
