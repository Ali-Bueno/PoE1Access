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
// Written 25 Nov 2026 from what is known of the game; the trials and the Labyrinth have not been
// tested with the mod yet, and the lessons say so.
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
                T("A Trial of Ascendancy is a short stretch of traps in some areas of the campaign, ending at a plaque you touch. The six of the first part, in Acts 1 to 3, open the Labyrinth, where you choose your Ascendancy class.",
                  "Una Prueba de Ascendencia es un tramo corto de trampas en algunas áreas de la campaña, que termina en una placa que tocas. Las seis de la primera parte, en los actos 1 a 3, abren el Laberinto, donde eliges tu clase de Ascendencia."),
                T("The first six are usually said to be in the Lower Prison in Act 1, the Crypt and the Chamber of Sins in Act 2, and the Crematorium, the Catacombs and the Imperial Gardens in Act 3. Later trials are in Acts 6 to 10, for the harder Labyrinths.",
                  "Se suele decir que las seis primeras están en la Prisión Inferior del acto 1, la Cripta y la Cámara de los Pecados del acto 2, y el Crematorio, las Catacumbas y los Jardines Imperiales del acto 3. Las siguientes están en los actos 6 a 10, para los Laberintos más difíciles."),
                T("Traps: spikes that rise from the floor, sawblades and spinning blades that move along tracks, blade sentries, darts from the walls and fire. Most move in a rhythm: wait, listen, then go through.",
                  "Trampas: pinchos que salen del suelo, sierras y cuchillas giratorias que se mueven por raíles, centinelas de cuchillas, dardos desde las paredes y fuego. La mayoría se mueven con un ritmo: espera, escucha y pasa."),
                T("With the mod: traps have a scanner kind of their own, Traps, and the two nearest tick from their side, faster as you get closer. The plaque is listed under Objects.",
                  "Con el mod: las trampas tienen su propio tipo en el escáner, Trampas, y las dos más cercanas hacen tic desde su lado, más rápido cuanto más te acercas. La placa se lista en Objetos del entorno."),
                T("This has not been tried in a trial yet. The mod writes everything it finds there to its log; sending the log after your first trial lets it be made exact.",
                  "Esto aún no se ha probado en una prueba. El mod escribe todo lo que encuentra allí en su registro; enviar el registro después de tu primera prueba permite ajustarlo."),
            },
        },
        new Lesson
        {
            Title = T("The Labyrinth and your Ascendancy", "El Laberinto y tu Ascendencia"),
            Pages = new[]
            {
                T("With the six trials done, the Lord's Labyrinth opens from the Aspirants' Plaza in Act 3, at the statue of the Goddess. It is a run of rooms full of traps, levers, locked doors and keys, where Izaro is fought several times.",
                  "Con las seis pruebas hechas, el Laberinto del Señor se abre desde la Plaza de los Aspirantes en el acto 3, en la estatua de la Diosa. Es una serie de salas llenas de trampas, palancas, puertas cerradas y llaves, donde se lucha varias veces contra Izaro."),
                T("Darkshrines along the way give a bonus for the run. Keys open locked doors and treasure. Beating Izaro at the end lets you choose your Ascendancy class, a small extra tree, with two points; harder Labyrinths later give two more each.",
                  "Los santuarios oscuros del camino dan una bonificación para el recorrido. Las llaves abren puertas cerradas y tesoros. Vencer a Izaro al final te deja elegir tu clase de Ascendencia, un pequeño árbol extra, con dos puntos; los Laberintos más difíciles dan dos más cada uno."),
                T("With the mod: traps, levers, keys and Darkshrines are in the scanner, and Izaro is an enemy with his life said as it falls. The Labyrinth's map and its puzzles have not been tried with the mod yet, and parts of it may be very hard to do by ear. Send the log after a try.",
                  "Con el mod: las trampas, palancas, llaves y santuarios oscuros están en el escáner, e Izaro es un enemigo cuya vida se dice a medida que baja. El mapa del Laberinto y sus acertijos aún no se han probado con el mod, y partes pueden ser muy difíciles de hacer de oído. Envía el registro después de intentarlo."),
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
        if (_nav != null && _nav.TrialHere) Tip("trial", T("Tip: this area has a Trial of Ascendancy, a stretch of traps ending at a plaque. Traps tick from their side and are under Traps in the scanner. More in the tutorial, Game mechanics.", "Consejo: esta área tiene una Prueba de Ascendencia, un tramo de trampas que termina en una placa. Las trampas hacen tic desde su lado y están en Trampas en el escáner. Más en el tutorial, Mecánicas del juego."));
        if (id.IndexOf("Labyrinth", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Aspirant", StringComparison.OrdinalIgnoreCase) >= 0)
            Tip("labyrinth", T("Tip: this is the way to the Labyrinth, where you choose your Ascendancy. More in the tutorial, Game mechanics.", "Consejo: este es el camino al Laberinto, donde eliges tu Ascendencia. Más en el tutorial, Mecánicas del juego."));
        if (act >= 6) Tip("resistances", T("Tip: finishing Act 5 lowered your resistances for good; gear with resistances matters now. More in the tutorial, Game mechanics.", "Consejo: terminar el acto 5 bajó tus resistencias para siempre; ahora importa el equipo con resistencias. Más en el tutorial, Mecánicas del juego."));
        if (id.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase)) Tip("maps", T("Tip: this is a map, the game after the campaign. More in the tutorial, Game mechanics.", "Consejo: esto es un mapa, el juego después de la campaña. Más en el tutorial, Mecánicas del juego."));
        if (_nav != null && _nav.NearCovenCauldron(60f)) Tip("covens", T("Tip: a Coven's Cauldron is near. Beat its coven, then choose a Fate at it. More in the tutorial, Game mechanics.", "Consejo: hay un Caldero del aquelarre cerca. Vence a su aquelarre y luego elige un Destino en él. Más en el tutorial, Mecánicas del juego."));
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
