using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExileCore.Shared.Enums;
using static PoE1Access.Loc;

namespace PoE1Access;

// The game's own mechanics, for a screen reader player: what each one is, in the order the game
// brings it in, and how it works with the mod. In the tutorial, under "Game mechanics". When you
// first meet one in play (your first town, a waypoint, a level up, a trial's trap, the
// Labyrinth, Act 2's bandits, maps...) a short tip says what it is, once, and that the tutorial
// has the rest. The tips seen are kept in app\mechanics-seen.txt (every character), and can be
// turned off with "Tips about new game mechanics" in the Speech submenu.
//
// Written 25 Nov 2026 from what is known of the game; the trials' places and traps are from the
// Path of Exile Wiki's "Trial of Ascendancy" page (last edited 24 May 2026), the Labyrinth's
// from its "The Lord's Labyrinth" page (last edited 19 Nov 2026). The trials and the
// Labyrinth have not been tested with the mod yet, and the lessons say so.
public partial class PoE1AccessCore
{
    private List<Lesson> MechanicsLessons() => new List<Lesson>
    {
        new Lesson
        {
            Title = T("Towns, characters and quests", "Ciudades, personajes y misiones"),
            Pages = new[]
            {
                T("Each act has a town, where no monster can reach you. Its characters sell, buy, give quests and rewards. The scanner lists them under Characters; one with something for you says it has a quest.",
                  "Cada acto tiene una ciudad, donde ningún monstruo puede alcanzarte. Sus personajes venden, compran, dan misiones y recompensas. El escáner los lista en Personajes; uno con algo para ti dice que tiene una misión."),
                T("Walk up to a character and the game's button prompt says how to talk. What they say, and each choice, is read; the game's quest rewards window is read like any menu.",
                  "Acércate a un personaje y la indicación de botones del juego dice cómo hablar. Se lee lo que dicen y cada opción; la ventana de recompensas de misión del juego se lee como cualquier menú."),
                T("Your current quest objectives come from the game's quest tracker. The Quest kind of the scanner holds what the game marks for your quest on the minimap, and an exit to the area your quest names says your quest leads there.",
                  "Tus objetivos de misión actuales vienen del registro de misiones del juego. El tipo Misión del escáner contiene lo que el juego marca para tu misión en el minimapa, y una salida al área que nombra tu misión dice que tu misión lleva allí."),
            },
        },
        new Lesson
        {
            Title = T("Waypoints and portals", "Transportadores y portales"),
            Pages = new[]
            {
                T("A waypoint, once you have touched it, takes you to any other waypoint you have found. Waypoints hum from their side, and the scanner lists them.",
                  "Un transportador, una vez que lo has tocado, te lleva a cualquier otro que hayas encontrado. Los transportadores zumban desde su lado y el escáner los lista."),
                T("At a waypoint the game opens its world map: the act and your quest are said as it opens, and each place as you move to it.",
                  "En un transportador el juego abre su mapa del mundo: al abrirse se dicen el acto y tu misión, y cada lugar al que te mueves."),
                T("A portal takes you to town and back to the same spot. Portals make a rising swirl and are listed under Exits.",
                  "Un portal te lleva a la ciudad y de vuelta al mismo sitio. Los portales hacen un remolino ascendente y se listan en Salidas."),
            },
        },
        new Lesson
        {
            Title = T("Skill gems, sockets and links", "Gemas de habilidad, engarces y enlaces"),
            Pages = new[]
            {
                T("Your skills come from gems put in the sockets of your gear. A socket's colour, red, green or blue, takes a gem of that colour. Quest rewards and vendors give new gems.",
                  "Tus habilidades vienen de gemas puestas en los engarces de tu equipo. El color de un engarce, rojo, verde o azul, admite una gema de ese color. Las recompensas de misión y los vendedores dan gemas nuevas."),
                T("Linked sockets share their gems: a support gem changes every skill gem linked to it. Gems gain experience and levels as you fight.",
                  "Los engarces enlazados comparten sus gemas: una gema de apoyo cambia cada gema de habilidad enlazada a ella. Las gemas ganan experiencia y niveles al luchar."),
                T("The mod reads an item's sockets and links with its description, and a gem's tags, level, cost and what it does. Put a skill on a button with the game's skill binding, R3, which the mod reads.",
                  "El mod lee los engarces y enlaces de un objeto con su descripción, y las etiquetas, el nivel, el coste y lo que hace una gema. Pon una habilidad en un botón con la asignación de habilidades del juego, R3, que el mod lee."),
            },
        },
        new Lesson
        {
            Title = T("Flasks", "Frascos"),
            Pages = new[]
            {
                T("Flasks are your potions: life, mana and others with special effects. They refill as you kill monsters, and in town.",
                  "Los frascos son tus pociones: de vida, de maná y otros con efectos especiales. Se rellenan al matar monstruos, y en la ciudad."),
                T("Double tap L3 to hear how many uses each flask has. The mod warns you when life or mana run low; using a flask is up to you, with the game's button.",
                  "Toca L3 dos veces para oír cuántos usos tiene cada frasco. El mod te avisa cuando la vida o el maná bajan; usar un frasco depende de ti, con el botón del juego."),
            },
        },
        new Lesson
        {
            Title = T("Levels and passive points", "Niveles y puntos de pasiva"),
            Pages = new[]
            {
                T("Each level gives a passive skill point to spend on the passive tree, a huge web of small and large bonuses shared by every class; you start where your class sits on it. Some quests give extra points, from a Book of Skill.",
                  "Cada nivel da un punto de habilidad pasiva para gastar en el árbol de pasivas, una enorme red de bonificaciones pequeñas y grandes que comparten todas las clases; empiezas donde está tu clase. Algunas misiones dan puntos extra, de un Libro de habilidad."),
                T("Only passives next to ones you have can be allocated. The mod reads each passive you move to, and the finder on the D-pad leads you to keystones, notables and the like. Nothing is allocated for you.",
                  "Solo se pueden asignar pasivas junto a las que ya tienes. El mod lee cada pasiva a la que te mueves, y el buscador de la cruceta te lleva a piedras angulares, notables y similares. No se asigna nada por ti."),
            },
        },
        new Lesson
        {
            Title = T("Currency, vendors and the stash", "Moneda, vendedores y el alijo"),
            Pages = new[]
            {
                T("There is no gold: currency items, such as orbs and scrolls, are the money, and most of them also change items. Vendors buy your items for currency.",
                  "No hay oro: los objetos de moneda, como orbes y pergaminos, son el dinero, y la mayoría también modifican objetos. Los vendedores compran tus objetos a cambio de moneda."),
                T("The stash in each town keeps items for all your characters. It clunks from its side; its windows and tabs are read like any menu.",
                  "El alijo de cada ciudad guarda objetos para todos tus personajes. Suena desde su lado; sus ventanas y pestañas se leen como cualquier menú."),
            },
        },
        new Lesson
        {
            Title = T("Act 2: the bandits", "Acto 2: los bandidos"),
            Pages = new[]
            {
                T("In Act 2 three bandit lords each offer a deal. Killing all three gives an extra passive point; helping one gives that bandit's reward instead.",
                  "En el acto 2, tres señores bandidos ofrecen cada uno un trato. Matar a los tres da un punto de pasiva extra; ayudar a uno da en su lugar la recompensa de ese bandido."),
                T("What each bandit says, and the choice to help or kill, is read like any dialogue.",
                  "Lo que dice cada bandido y la opción de ayudar o matar se leen como cualquier diálogo."),
            },
        },
        new Lesson
        {
            Title = T("Trials of Ascendancy", "Pruebas de Ascendencia"),
            Pages = new[]
            {
                T("A Trial of Ascendancy is a short stretch of traps in some areas of the campaign, ending at a plaque you touch. Each trial has one kind of trap. Trials you finish count for all your characters in the league.",
                  "Una Prueba de Ascendencia es un tramo corto de trampas en algunas áreas de la campaña, que termina en una placa que tocas. Cada prueba tiene un tipo de trampa. Las pruebas que terminas cuentan para todos tus personajes de la liga."),
                T("The six for the first, Normal, Labyrinth: the Lower Prison in Act 1, spike traps. The Crypt Level 1 in Act 2, spinning blades. The Chamber of Sins Level 2 in Act 2, sawblades. The Crematorium in Act 3, furnace traps. The Catacombs in Act 3, blade sentries. The Imperial Gardens in Act 3, dart traps.",
                  "Las seis del primer Laberinto, el Normal: la Prisión Inferior en el acto 1, trampas de pinchos. La Cripta nivel 1 en el acto 2, cuchillas giratorias. La Cámara de los Pecados nivel 2 en el acto 2, sierras. El Crematorio en el acto 3, trampas de horno. Las Catacumbas en el acto 3, centinelas de cuchillas. Los Jardines Imperiales en el acto 3, trampas de dardos."),
                T("For the Cruel Labyrinth: the Prison in Act 6, spike traps; the Crypt in Act 7, spinning blades; the Chamber of Sins Level 2 in Act 7, sawblades. For the Merciless Labyrinth: the Bath House in Act 8, furnace traps; the Tunnel in Act 9, blade sentries; the Ossuary in Act 10, dart traps.",
                  "Para el Laberinto Cruel: la Prisión en el acto 6, trampas de pinchos; la Cripta en el acto 7, cuchillas giratorias; la Cámara de los Pecados nivel 2 en el acto 7, sierras. Para el Laberinto Despiadado: la Casa de Baños en el acto 8, trampas de horno; el Túnel en el acto 9, centinelas de cuchillas; el Osario en el acto 10, trampas de dardos."),
                T("After the campaign, harder trials appear at random in yellow and red maps; finishing one gives an Offering to the Goddess, the way into the Eternal Labyrinth. They can hold Sentinel traps, which you switch off for a while by damaging them.",
                  "Después de la campaña aparecen pruebas más difíciles al azar en mapas amarillos y rojos; terminar una da una Ofrenda a la Diosa, la entrada al Laberinto Eterno. Pueden tener trampas centinela, que se apagan un rato dañándolas."),
                T("Traps: spikes that rise from the floor, spinning blades and sawblades that move along their tracks, blade sentries, furnaces that heat the floor with lava, and darts from the walls. Most move in a rhythm: wait, listen, then go through. Each is explained in the lesson Labyrinth traps.",
                  "Trampas: pinchos que salen del suelo, cuchillas giratorias y sierras que se mueven por sus raíles, centinelas de cuchillas, hornos que calientan el suelo con lava y dardos desde las paredes. La mayoría se mueven con un ritmo: espera, escucha y pasa. Cada una se explica en la lección Trampas del Laberinto."),
                T("With the mod: entering one of these areas says that it has a trial and which trap. Traps have a scanner kind of their own, Traps, and the two nearest tick from their side, faster as you get closer. The plaque is listed under Objects.",
                  "Con el mod: al entrar en una de estas áreas se dice que tiene una prueba y qué trampa. Las trampas tienen su propio tipo en el escáner, Trampas, y las dos más cercanas hacen tic desde su lado, más rápido cuanto más te acercas. La placa se lista en Objetos del entorno."),
                T("With the mod: traps have a scanner kind of their own, Traps, and the two nearest tick from their side, faster as you get closer. The plaque is listed under Objects.",
                  "Con el mod: las trampas tienen su propio tipo en el escáner, Trampas, y las dos más cercanas hacen tic desde su lado, más rápido cuanto más te acercas. La placa se lista en Objetos del entorno."),
                T("This has not been tried in a trial yet. The mod writes everything it finds there to its log; sending the log after your first trial lets it be made exact.",
                  "Esto aún no se ha probado en una prueba. El mod escribe todo lo que encuentra allí en su registro; enviar el registro después de tu primera prueba permite ajustarlo."),
            },
        },
        new Lesson
        {
            Title = T("The Labyrinth: getting in and its rules", "El Laberinto: cómo entrar y sus reglas"),
            Pages = new[]
            {
                T("With the six trials done, the Statue of the Goddess in the Sarn Encampment, in Act 3, takes you to the Aspirants' Plaza. The plaza has a waypoint, so you can come back from any act. Its window lets you choose which Labyrinth to enter and shows the trials you have done.",
                  "Con las seis pruebas hechas, la Estatua de la Diosa del Campamento de Sarn, en el acto 3, te lleva a la Plaza de los Aspirantes. La plaza tiene un transportador, así que puedes volver desde cualquier acto. Su ventana te deja elegir en qué Laberinto entrar y muestra las pruebas que has hecho."),
                T("A Labyrinth must be done in one run, without dying. There are no waypoints or checkpoints in it. Leave it for any reason, death, a portal, logging out or a crash, and you cannot go back: you start a new one. Leaving by portal asks you first.",
                  "Un Laberinto se hace en un solo recorrido, sin morir. No tiene transportadores ni puntos de control. Si lo dejas por cualquier motivo, morir, un portal, cerrar la sesión o un fallo, no puedes volver: empiezas uno nuevo. Salir por un portal te lo pregunta antes."),
                T("Your stash is there just before each fight with Izaro, and at the end. Keys and the Labyrinth's own items only work in that run, and are lost when you leave.",
                  "Tu alijo está justo antes de cada combate contra Izaro, y al final. Las llaves y los objetos propios del Laberinto solo sirven en ese recorrido, y se pierden al salir."),
                T("The layout changes every day at midnight, world time. Every run of that Labyrinth on the same day has the same chambers in the same order, so a second try the same day follows the way you have already learned.",
                  "La disposición cambia cada día a medianoche, hora mundial. Cada recorrido de ese Laberinto el mismo día tiene las mismas salas en el mismo orden, así que un segundo intento el mismo día sigue el camino que ya has aprendido."),
                T("Four Labyrinths, each harder: the Normal one, area level 33; the Cruel one, from character level 40, after the Normal one and its three trials; the Merciless one, from level 50, after the Cruel one and its three trials; and the Eternal one, from level 68, after the Merciless one, which takes an Offering to the Goddess each time.",
                  "Cuatro Laberintos, cada uno más difícil: el Normal, de nivel de área 33; el Cruel, desde el nivel de personaje 40, después del Normal y sus tres pruebas; el Despiadado, desde el nivel 50, después del Cruel y sus tres pruebas; y el Eterno, desde el nivel 68, después del Despiadado, que pide una Ofrenda a la Diosa cada vez."),
            },
        },
        new Lesson
        {
            Title = T("The Labyrinth: chambers, keys and Izaro", "El Laberinto: salas, llaves e Izaro"),
            Pages = new[]
            {
                T("The Labyrinth is a run of chambers joined by exits. A chamber's name says how many ways it has: Annex is a dead end with one, Passage has two, Halls three and Atrium four. The first word is its look: Basilica, Sepulchre, Sanitorium, Mansion, Estate or Domain.",
                  "El Laberinto es una serie de salas unidas por salidas. El nombre de una sala dice cuántos caminos tiene: Anexo es un callejón sin salida con uno, Pasaje tiene dos, Salones tres y Atrio cuatro. La primera palabra es su aspecto: Basílica, Sepulcro, Sanatorio, Mansión, Finca o Dominio."),
                T("By each door a pedestal shows the map of the chambers you have seen and the one behind that door, with what it holds: an Aspirant's Trial, a Forgotten Reliquary, a Golden Door, a Silver Cache, or nothing special.",
                  "Junto a cada puerta, un pedestal muestra el mapa de las salas que has visto y la que hay detrás de esa puerta, con lo que contiene: una Prueba del Aspirante, un Relicario Olvidado, una Puerta Dorada, un Alijo de Plata o nada especial."),
                T("Izaro is fought three times, in the Aspirant's Trials: the first ends when he is down to two thirds of his life, the second at one third, the third when he dies. Each fight has a waiting room with a stash before the arena; once in the arena you cannot go back until it is over.",
                  "Contra Izaro se lucha tres veces, en las Pruebas del Aspirante: la primera termina cuando le queda dos tercios de su vida, la segunda un tercio, la tercera cuando muere. Cada combate tiene una sala de espera con alijo antes de la arena; una vez en la arena no puedes volver hasta que termine."),
                T("In his fights, Izaro is helped by devices and monsters that make him stronger. Destroying them makes the fight easier; leaving them carries them to the next fight, harder, but he drops an extra Treasure Key for each one at the end. A Forgotten Reliquary's Decorative Chest holds a trinket that removes one of them.",
                  "En sus combates, a Izaro le ayudan artefactos y monstruos que lo hacen más fuerte. Destruirlos hace el combate más fácil; dejarlos los pasa al siguiente combate, más difícil, pero al final suelta una Llave del Tesoro extra por cada uno. El Cofre Decorativo de un Relicario Olvidado tiene un abalorio que quita uno de ellos."),
                T("A Golden Door blocks the way on and needs the Golden Key, found in the same chamber, or next to it in the Eternal Labyrinth. A Silver Cache is an optional locked room with treasure or a Darkshrine, opened with a Silver Key. Darkshrines give a bonus for the rest of the run. From the Merciless Labyrinth on, Argus, a strong monster, drops a Treasure Key.",
                  "Una Puerta Dorada bloquea el camino y necesita la Llave Dorada, que está en la misma sala, o al lado en el Laberinto Eterno. Un Alijo de Plata es una sala cerrada opcional con tesoro o un santuario oscuro, que se abre con una Llave de Plata. Los santuarios oscuros dan una bonificación para el resto del recorrido. Desde el Laberinto Despiadado, Argus, un monstruo fuerte, suelta una Llave del Tesoro."),
                T("A tile puzzle room shows three tiles, such as fire, lightning and ice, to step on in order; then you cross a grid stepping on tiles in that same order, over and over. A wrong tile fires darts and starts it again. Some chambers hide switches and secret passages that are not marked.",
                  "Una sala de acertijo de baldosas muestra tres baldosas, como fuego, rayo y hielo, para pisarlas en orden; luego cruzas una cuadrícula pisando baldosas en ese mismo orden, una y otra vez. Una baldosa equivocada dispara dardos y lo reinicia. Algunas salas esconden interruptores y pasajes secretos sin marcar."),
            },
        },
        new Lesson
        {
            Title = T("The Labyrinth: the end and your Ascendancy", "El Laberinto: el final y tu Ascendencia"),
            Pages = new[]
            {
                T("After Izaro's last fight, the Ascendancy Chamber holds the Altar of Ascendancy, a Divine Font and ten chests of Izaro's Treasure.",
                  "Después del último combate contra Izaro, la Sala de Ascendencia tiene el Altar de Ascendencia, una Fuente Divina y diez cofres del Tesoro de Izaro."),
                T("At the Altar, your first Labyrinth lets you choose your Ascendancy class, a small extra passive tree, and each Labyrinth the first time gives two Ascendancy points: eight in all.",
                  "En el Altar, tu primer Laberinto te deja elegir tu clase de Ascendencia, un pequeño árbol de pasivas extra, y cada Laberinto la primera vez da dos puntos de Ascendencia: ocho en total."),
                T("The Divine Font changes one of your gems each time you finish a Labyrinth: it can turn a skill gem into a transfigured version, a different form of that skill, add quality or experience, or trade the gem for currency or Treasure Keys. Its options are a window, read like any menu.",
                  "La Fuente Divina cambia una de tus gemas cada vez que terminas un Laberinto: puede convertir una gema de habilidad en una versión transfigurada, otra forma de esa habilidad, añadir calidad o experiencia, o cambiar la gema por moneda o Llaves del Tesoro. Sus opciones son una ventana, leída como cualquier menú."),
                T("Each of Izaro's Treasure chests takes one Treasure Key. Keys come from chests and lockboxes in the Labyrinth, from Izaro, and from Argus.",
                  "Cada cofre del Tesoro de Izaro necesita una Llave del Tesoro. Las llaves salen de cofres y cajas en el Laberinto, de Izaro y de Argus."),
                T("With the mod: traps, pressure plates, levers, keys, Darkshrines, the Altar and the Font are in the scanner; chests are under Chests; Izaro is an enemy with his life said as it falls. A chamber's exits say what their name means, such as a dead end. The pedestal map and the tile puzzle have not been tried with the mod yet, and parts of the Labyrinth may be very hard by ear. Send the log after a try.",
                  "Con el mod: las trampas, placas de presión, palancas, llaves, santuarios oscuros, el Altar y la Fuente están en el escáner; los cofres en Cofres; Izaro es un enemigo cuya vida se dice a medida que baja. Las salidas de una sala dicen lo que significa su nombre, como un callejón sin salida. El mapa de los pedestales y el acertijo de baldosas aún no se han probado con el mod, y partes del Laberinto pueden ser muy difíciles de oído. Envía el registro después de intentarlo."),
            },
        },
        new Lesson
        {
            Title = T("Labyrinth traps", "Trampas del Laberinto"),
            Pages = new[]
            {
                T("Traps hurt by a share of your life and energy shield, so they stay dangerous at any level. Each kind is first shown in one of the trials. Traps hurt monsters too, minions less.",
                  "Las trampas hacen daño según una parte de tu vida y escudo de energía, así que siguen siendo peligrosas a cualquier nivel. Cada tipo se presenta primero en una de las pruebas. Las trampas también dañan a los monstruos, menos a los esbirros."),
                T("Spike traps shoot up from a square of floor on a timer or when you step on their pressure plate: a quarter of your health, bleeding, and you are held in place. While up they block the way.",
                  "Las trampas de pinchos salen de un cuadrado del suelo con un temporizador o al pisar su placa de presión: un cuarto de tu salud, sangrado, y te quedas inmovilizado. Mientras están arriba bloquean el paso."),
                T("Sawblades run along a straight track and kill in about a second of contact; nearby levers often switch them on and off. Spinning blades follow a track that turns or runs diagonally, as deadly; levers can send them onto another track.",
                  "Las sierras recorren un raíl recto y matan en un segundo de contacto; las palancas cercanas a menudo las encienden y apagan. Las cuchillas giratorias siguen un raíl que gira o va en diagonal, igual de mortales; las palancas pueden desviarlas a otro raíl."),
                T("Furnace traps heat the floor with lava now and then: standing on it burns you. Fire skeletons come out of them, unharmed by the heat. Blade sentries are wide blades moving along set paths, on the ground or flying, worst at their centre.",
                  "Las trampas de horno calientan el suelo con lava de vez en cuando: quedarte encima te quema. De ellas salen esqueletos de fuego, a los que el calor no daña. Los centinelas de cuchillas son cuchillas anchas que se mueven por caminos fijos, por el suelo o volando, peores en su centro."),
                T("Dart traps sit on walls and pillars and fire on a timer or from a hidden pressure plate: poison, and they slow you a lot. Cleaver traps, along narrow corridors, slam a big blade down every few seconds, with strong bleeding.",
                  "Las trampas de dardos están en paredes y pilares y disparan con un temporizador o desde una placa de presión oculta: veneno, y te ralentizan mucho. Las trampas de cuchilla, en pasillos estrechos, dejan caer una gran hoja cada pocos segundos, con un sangrado fuerte."),
                T("Sentinel traps, only in the map trials and the Eternal Labyrinth, put an effect on the ground around them, such as draining flask charges, novas of fire, ice or lightning, slowing curses or more damage taken. Damaging one switches it off for a while.",
                  "Las trampas centinela, solo en las pruebas de mapas y el Laberinto Eterno, ponen un efecto en el suelo a su alrededor, como quitar cargas de frasco, novas de fuego, hielo o rayo, maldiciones que ralentizan o más daño recibido. Dañar una la apaga un rato."),
                T("Most traps move in a rhythm. With the mod, the two nearest tick from their side, faster as you get closer: stop, listen for the rhythm, then cross. Each trap is named as you come near.",
                  "La mayoría de las trampas se mueven con un ritmo. Con el mod, las dos más cercanas hacen tic desde su lado, más rápido cuanto más te acercas: para, escucha el ritmo y cruza. Cada trampa se nombra al acercarte."),
            },
        },
        new Lesson
        {
            Title = T("Later in the campaign: resistances and the Pantheon", "Más adelante en la campaña: resistencias y el Panteón"),
            Pages = new[]
            {
                T("Finishing Act 5, and again Act 10, lowers your fire, cold and lightning resistances for good. Gear with resistances matters from then on; the character sheet, read with L3 and the right stick, shows them.",
                  "Al terminar el acto 5, y otra vez el acto 10, tus resistencias al fuego, al frío y al rayo bajan para siempre. A partir de entonces importa el equipo con resistencias; la hoja de personaje, leída con L3 y el stick derecho, las muestra."),
                T("In the second half of the campaign, beating certain gods opens the Pantheon: one major and one minor power to choose, changed in its window, which is read like any menu.",
                  "En la segunda mitad de la campaña, vencer a ciertos dioses abre el Panteón: un poder mayor y uno menor para elegir, que se cambian en su ventana, leída como cualquier menú."),
            },
        },
        new Lesson
        {
            Title = T("After the campaign: maps and the Atlas", "Después de la campaña: mapas y el Atlas"),
            Pages = new[]
            {
                T("After Act 10 the game goes on in maps: items you put in the map device to open an area. Finishing maps opens more of the Atlas, with its own passive tree.",
                  "Después del acto 10 el juego sigue en mapas: objetos que pones en el dispositivo de mapas para abrir un área. Terminar mapas abre más del Atlas, con su propio árbol de pasivas."),
                T("Maps are areas like the campaign's, so the scanner, the radar and the guide work the same. The Atlas screen and the map device have not been tried with the mod yet.",
                  "Los mapas son áreas como las de la campaña, así que el escáner, el radar y la guía funcionan igual. La pantalla del Atlas y el dispositivo de mapas aún no se han probado con el mod."),
            },
        },
        new Lesson
        {
            Title = T("This league: the Covens", "Esta liga: los Aquelarres"),
            Pages = new[]
            {
                T("Coven's Cauldrons appear in areas, guarded by a coven of witches. Beat the coven, then choose a Fate at the cauldron: each card makes the area's monsters stronger in one way and gives a reward.",
                  "Los Calderos del aquelarre aparecen en las áreas, custodiados por un aquelarre de brujas. Vence al aquelarre y luego elige un Destino en el caldero: cada carta hace a los monstruos del área más fuertes de una forma y da una recompensa."),
                T("With the mod: cauldrons bubble from their side and say whether they are guarded or ready. The Fate window reads each card, its tier and its reward as you move.",
                  "Con el mod: los calderos burbujean desde su lado y dicen si están custodiados o listos. La ventana de Destinos lee cada carta, su nivel y su recompensa al moverte."),
            },
        },
    };

    // ---- Tips the first time you meet a mechanic ----

    private HashSet<string> _mechanicsSeen;
    private string _mechanicsFile;
    private long _nextMechanicsMs;
    private bool _tipSaid;   // one tip at a time, two seconds apart

    private void PollMechanicTips(long now, bool uiBusy)
    {
        if (!Settings.MechanicTips.Value || uiBusy || now < _nextMechanicsMs) return;
        _nextMechanicsMs = now + 2000;
        _tipSaid = false;
        if (_mechanicsSeen == null)
        {
            _mechanicsSeen = new HashSet<string>();
            try
            {
                _mechanicsFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mechanics-seen.txt"));
                if (File.Exists(_mechanicsFile)) foreach (string l in File.ReadAllLines(_mechanicsFile)) _mechanicsSeen.Add(l.Trim());
            }
            catch { }
        }

        var area = Try(() => GameController.Area.CurrentArea, null);
        if (area == null) return;
        string id = Try(() => area.Area.Id, "") ?? "";
        string name = Try(() => area.Name, "") ?? "";
        int act = Try(() => area.Area.Act, 0);
        int level = Try(() => GameController.Player.GetComponent<ExileCore.PoEMemory.Components.Player>().Level, 0);

        if (Try(() => area.IsTown, false)) Tip("town", T("Tip: this is a town, safe from monsters, with vendors, quests and the stash. More in the tutorial, Game mechanics.", "Consejo: esto es una ciudad, a salvo de monstruos, con vendedores, misiones y el alijo. Más en el tutorial, Mecánicas del juego."));
        else if (_nav != null && _nav.NearKind(EntityType.Waypoint, 50f)) Tip("waypoint", T("Tip: a waypoint is near. Touch it once and you can travel to it from any other. More in the tutorial, Game mechanics.", "Consejo: hay un transportador cerca. Tócalo una vez y podrás viajar a él desde cualquier otro. Más en el tutorial, Mecánicas del juego."));
        if (level >= 2) Tip("passives", T("Tip: levels give passive skill points, spent on the passive tree in its panel. More in the tutorial, Game mechanics.", "Consejo: los niveles dan puntos de habilidad pasiva, que se gastan en el árbol de pasivas en su panel. Más en el tutorial, Mecánicas del juego."));
        if (act == 2) Tip("bandits", T("Tip: in this act three bandit lords offer deals; killing all three gives a passive point. More in the tutorial, Game mechanics.", "Consejo: en este acto tres señores bandidos ofrecen tratos; matar a los tres da un punto de pasiva. Más en el tutorial, Mecánicas del juego."));
        // The campaign's trials, by their area (the game's English names).
        string trap = TrialTrap(name, act);
        if (trap.Length > 0) Tip("trial:" + act + ":" + name, T($"This area has a Trial of Ascendancy, with {trap}. It ends at a plaque. More in the tutorial, Game mechanics.", $"Esta área tiene una Prueba de Ascendencia, con {trap}. Termina en una placa. Más en el tutorial, Mecánicas del juego."));
        if (_nav != null && _nav.TrialHere) Tip("trial", T("Tip: this area has a Trial of Ascendancy, a stretch of traps ending at a plaque. Traps tick from their side and are under Traps in the scanner. More in the tutorial, Game mechanics.", "Consejo: esta área tiene una Prueba de Ascendencia, un tramo de trampas que termina en una placa. Las trampas hacen tic desde su lado y están en Trampas en el escáner. Más en el tutorial, Mecánicas del juego."));
        if (id.IndexOf("Labyrinth", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Aspirant", StringComparison.OrdinalIgnoreCase) >= 0)
            Tip("labyrinth", T("Tip: this is the way to the Labyrinth, where you choose your Ascendancy. More in the tutorial, Game mechanics.", "Consejo: este es el camino al Laberinto, donde eliges tu Ascendencia. Más en el tutorial, Mecánicas del juego."));
        if (act >= 6) Tip("resistances", T("Tip: finishing Act 5 lowered your resistances for good; gear with resistances matters now. More in the tutorial, Game mechanics.", "Consejo: terminar el acto 5 bajó tus resistencias para siempre; ahora importa el equipo con resistencias. Más en el tutorial, Mecánicas del juego."));
        if (Navigator.IsLabChamber(name))
            Tip("labyrinth run", T("Tip: you are in the Labyrinth. Do it in one run: dying or leaving ends it. Your stash is just before each fight with Izaro. A chamber's exits say what their names mean. More in the tutorial, Game mechanics.", "Consejo: estás en el Laberinto. Hazlo en un solo recorrido: morir o salir lo termina. Tu alijo está justo antes de cada combate contra Izaro. Las salidas de una sala dicen lo que significa su nombre. Más en el tutorial, Mecánicas del juego."));
        if (id.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase)) Tip("maps", T("Tip: this is a map, the game after the campaign. More in the tutorial, Game mechanics.", "Consejo: esto es un mapa, el juego después de la campaña. Más en el tutorial, Mecánicas del juego."));
        if (_nav != null && _nav.NearCovenCauldron(60f)) Tip("covens", T("Tip: a Coven's Cauldron is near. Beat its coven, then choose a Fate at it. More in the tutorial, Game mechanics.", "Consejo: hay un Caldero del aquelarre cerca. Vence a su aquelarre y luego elige un Destino en él. Más en el tutorial, Mecánicas del juego."));
    }

    // The campaign's Trials of Ascendancy (Path of Exile Wiki, 24 May 2026): the trap of the trial
    // in an area, or "" when it has none.
    private static string TrialTrap(string areaName, int act)
    {
        string n = (areaName ?? "").Trim();
        if (n.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) n = n.Substring(4);
        string spikes = T("spike traps", "trampas de pinchos"), spinning = T("spinning blades", "cuchillas giratorias"),
               saws = T("sawblades", "sierras"), furnace = T("furnace traps", "trampas de horno"),
               sentries = T("blade sentries", "centinelas de cuchillas"), darts = T("dart traps", "trampas de dardos");
        return (n.ToLowerInvariant(), act) switch
        {
            ("lower prison", 1) => spikes,
            ("crypt level 1", 2) => spinning,
            ("chamber of sins level 2", 2) => saws,
            ("crematorium", 3) => furnace,
            ("catacombs", 3) => sentries,
            ("imperial gardens", 3) => darts,
            ("prison", 6) => spikes,
            ("crypt", 7) => spinning,
            ("chamber of sins level 2", 7) => saws,
            ("bath house", 8) => furnace,
            ("tunnel", 9) => sentries,
            ("ossuary", 10) => darts,
            _ => "",
        };
    }

    private void Tip(string key, string text)
    {
        if (_tipSaid || _mechanicsSeen == null || !_mechanicsSeen.Add(key)) return;
        _tipSaid = true;
        try { if (_mechanicsFile != null) File.AppendAllText(_mechanicsFile, key + Environment.NewLine); } catch { }
        PluginLog.Write("Mechanic tip: " + key);
        Say(text, false);
    }
}
