using System.Collections.Generic;
using static PoE1Access.Loc;

namespace PoE1Access;

// Step-by-step guides in the tutorial ("Step by step"): the game's busiest screens and tasks, the
// passive tree, items, socketing gems, links, skill binding, vendors, the stash, quest rewards and
// the character sheet, walked through with what the mod says at each step. They tell you what to
// listen for, not which of the game's buttons to press: those come from the game's own button
// prompts, which the mod reads as each window opens (hold L3 to hear them again), so the guides
// stay right when the game changes its buttons.
public partial class PoE1AccessCore
{
    private List<Lesson> Guides() => new List<Lesson>
    {
        new Lesson
        {
            Title = T("The passive tree: reading it", "El árbol de pasivas: cómo leerlo"),
            Pages = new[]
            {
                T("The passive tree is big, but you only ever need the part next to what you have. Every class shares one tree and starts at its own place on it. Each level gives one point, and a point can only go on a passive linked to one you already have.",
                  "El árbol de pasivas es grande, pero solo necesitas la parte junto a lo que ya tienes. Todas las clases comparten un árbol y empiezan en su propio sitio. Cada nivel da un punto, y un punto solo puede ir en una pasiva enlazada a una que ya tienes."),
                T("Open the tree with the game's button and move with the game's stick. Each passive you move to is read: its name, its kind, then its state, then what it does.",
                  "Abre el árbol con el botón del juego y muévete con el stick del juego. Se lee cada pasiva a la que te mueves: su nombre, su tipo, su estado y lo que hace."),
                T("The kinds: a passive is a small bonus; a notable is a larger one; a keystone changes a rule of the game, often with a cost; a mastery lets you pick one bonus from a list; a jewel socket holds a jewel; and a class start is where a class begins.",
                  "Los tipos: una pasiva es una bonificación pequeña; una notable es mayor; una piedra angular cambia una regla del juego, a menudo con un coste; una maestría te deja elegir una bonificación de una lista; un engarce de joya admite una joya; y un inicio de clase es donde empieza una clase."),
                T("The states: allocated is yours; can be allocated means you can spend a point on it now; linked means it touches one of yours; not linked says how many points away it is.",
                  "Los estados: asignada es tuya; se puede asignar significa que puedes gastar un punto en ella ahora; enlazada significa que toca una de las tuyas; sin enlazar dice a cuántos puntos está."),
                T("Last, the passives linked to it are said with the way each lies, going round clockwise from up, and their states. That is your map: follow a link's direction with the stick to reach it.",
                  "Al final se dicen las pasivas enlazadas con ella, con la dirección de cada una, en el sentido de las agujas del reloj desde arriba, y sus estados. Ese es tu mapa: sigue la dirección de un enlace con el stick para llegar."),
                T("Tap L3 for the next line of a passive, double tap for the line before, and hold L3 for all of it. Nothing is ever allocated for you: you choose and confirm with the game's button.",
                  "Toca L3 para la siguiente línea de una pasiva, dos veces para la anterior, y mantén L3 para oírla entera. Nunca se asigna nada por ti: eliges y confirmas con el botón del juego."),
            },
        },
        new Lesson
        {
            Title = T("The passive tree: finding your way with the finder", "El árbol de pasivas: orientarte con el buscador"),
            Pages = new[]
            {
                T("With the tree open, the D-pad is the mod's finder. Up and down choose a list: Position, Keystones, Notables, Jewel sockets, and Next to your nodes, the ones you can take now.",
                  "Con el árbol abierto, la cruceta es el buscador del mod. Arriba y abajo eligen una lista: Posición, Piedras angulares, Notables, Engarces de joya y Junto a tus nodos, los que puedes coger ahora."),
                T("Left and right go through the list, the ones costing fewest points first. Each says how many points away it is and which way it lies.",
                  "Izquierda y derecha recorren la lista, primero los que cuestan menos puntos. Cada uno dice a cuántos puntos está y en qué dirección."),
                T("The one named becomes your target. From then on, every passive you move to ends with the way on: toward your target, or, while it is not linked yet, toward the next passive to allocate on the cheapest way there, said as Allocate first.",
                  "El nombrado pasa a ser tu objetivo. Desde entonces, cada pasiva a la que te mueves termina con el camino a seguir: hacia tu objetivo o, mientras no esté enlazado, hacia la siguiente pasiva que asignar por el camino más barato, dicha como Asigna primero."),
                T("You hear Arrived when you reach it, and once you have allocated it, that you have it, and the guide turns off. Choose Position to drop the target and hear where you are.",
                  "Oyes Has llegado cuando lo alcanzas y, una vez asignado, que ya lo tienes, y la guía se apaga. Elige Posición para quitar el objetivo y oír dónde estás."),
                T("A way to plan: pick a keystone or notable that suits your skill, follow Allocate first point by point as you level, and read each passive on the way so you know what you are getting.",
                  "Una forma de planear: elige una piedra angular o notable que vaya con tu habilidad, sigue Asigna primero punto a punto al subir de nivel, y lee cada pasiva del camino para saber qué obtienes."),
            },
        },
        new Lesson
        {
            Title = T("Your inventory and items", "Tu inventario y tus objetos"),
            Pages = new[]
            {
                T("In the inventory, each item you move to is read with where it is, its column and row, then anything you lack to use it, such as Cannot use, 21 Intelligence, then its description.",
                  "En el inventario, cada objeto al que te mueves se lee con dónde está, su columna y fila, luego lo que te falta para usarlo, como No se puede usar, 21 de Inteligencia, y después su descripción."),
                T("Descriptions come from the game's data: name and rarity, then quality, sockets and links, damage or defences, requirements, and the mods. Tap L3 for the next line, double tap for the one before, hold for all of it.",
                  "Las descripciones vienen de los datos del juego: nombre y rareza, luego calidad, engarces y enlaces, daño o defensas, requisitos y los modificadores. Toca L3 para la siguiente línea, dos veces para la anterior, mantén para oírla entera."),
                T("Your equipment is read by slot, such as Helmet: Iron Hat. Hold L3 for a second and a half in the inventory for every item in your bag, or every equipped slot when you are on your equipment.",
                  "Tu equipo se lee por ranura, como Casco: Sombrero de hierro. Mantén L3 un segundo y medio en el inventario para oír todos los objetos de tu bolsa, o todas las ranuras equipadas cuando estás en tu equipo."),
                T("Moving items is said as it happens: In hand when you pick one up, then where it went, Equipped or Placed at a column and row. What arrives or leaves without your hand is said too: Received, or Handed over.",
                  "Mover objetos se dice al hacerlo: En la mano al cogerlo, luego adónde fue, Equipado o Colocado en una columna y fila. Lo que llega o se va sin pasar por tu mano también se dice: Recibido o Entregado."),
                T("An item's other actions, such as unsocket, split or sell, open from the game's button for them; the mod says More actions and lists them. From the keyboard, F5 and F6 go through your bag and F7 switches to your equipment.",
                  "Las otras acciones de un objeto, como desengarzar, dividir o vender, se abren con el botón del juego para ellas; el mod dice Más acciones y las enumera. Desde el teclado, F5 y F6 recorren tu bolsa y F7 cambia a tu equipo."),
            },
        },
        new Lesson
        {
            Title = T("Socketing a gem, step by step", "Engarzar una gema, paso a paso"),
            Pages = new[]
            {
                T("A gem goes in a socket of the same colour: red, green or blue; a white socket takes any. A gem's description says its colour through its attribute: strength gems are red, dexterity green, intelligence blue.",
                  "Una gema va en un engarce de su mismo color: rojo, verde o azul; un engarce blanco admite cualquiera. La descripción de una gema dice su color por su atributo: las gemas de fuerza son rojas, las de destreza verdes, las de inteligencia azules."),
                T("Step 1. In the inventory, move to the item you want to put the gem in, and listen to its sockets: how many, their colours, and which are linked.",
                  "Paso 1. En el inventario, muévete al objeto en el que quieres poner la gema y escucha sus engarces: cuántos, sus colores y cuáles están enlazados."),
                T("Step 2. Open the item's sockets with the game's action for it: the mod reads the actions as More actions, and the game's prompts as Controls. Sockets is among them.",
                  "Paso 2. Abre los engarces del objeto con la acción del juego para ello: el mod lee las acciones como Más acciones, y las indicaciones del juego como Controles. Engarces está entre ellas."),
                T("Step 3. In the item's sockets, move between them: each is read as Socket 2 of 3, its colour, and the gem in it, or empty.",
                  "Paso 3. En los engarces del objeto, muévete entre ellos: cada uno se lee como Engarce 2 de 3, su color, y la gema que tiene, o vacío."),
                T("Step 4. On an empty socket of the right colour, the game lets you choose a gem from your bag: listen to its prompts, and whatever you move to is read. Once the gem is in, moving back to that socket reads it with the gem.",
                  "Paso 4. En un engarce vacío del color correcto, el juego te deja elegir una gema de tu bolsa: escucha sus indicaciones, y se lee lo que seleccionas. Una vez dentro la gema, volver a ese engarce lo lee con la gema."),
                T("To take a gem out, its socket's actions offer to unsocket it or pick it up. If the game's buttons are not what you expected, hold L3 at any step to hear the game's prompts for that screen.",
                  "Para sacar una gema, las acciones de su engarce ofrecen desengarzarla o cogerla. Si los botones del juego no son los que esperabas, mantén L3 en cualquier paso para oír las indicaciones del juego para esa pantalla."),
            },
        },
        new Lesson
        {
            Title = T("Links and support gems", "Enlaces y gemas de apoyo"),
            Pages = new[]
            {
                T("Sockets can be linked together. A support gem changes every skill gem linked to it: more damage, more projectiles, added fire, and so on. A support gem on its own does nothing.",
                  "Los engarces pueden estar enlazados. Una gema de apoyo cambia cada gema de habilidad enlazada a ella: más daño, más proyectiles, fuego añadido, y así. Una gema de apoyo sola no hace nada."),
                T("An item's description says its links, and a gem's description gives its tags, such as Attack, Projectile or Fire. A support only works with skills whose tags it supports, which its own description says.",
                  "La descripción de un objeto dice sus enlaces, y la de una gema da sus etiquetas, como Ataque, Proyectil o Fuego. Un apoyo solo funciona con habilidades cuyas etiquetas apoya, lo que dice su propia descripción."),
                T("In the skills panel, hold L3 and use the right stick: left and right go through your skills, up and down through each one's details, ending with the supports linked to it. That tells you a support is really working.",
                  "En el panel de habilidades, mantén L3 y usa el stick derecho: izquierda y derecha recorren tus habilidades, arriba y abajo los detalles de cada una, terminando con los apoyos enlazados. Así sabes que un apoyo está funcionando de verdad."),
                T("Supports also raise a skill's cost. If a skill says it costs more mana than you have, it cannot be used; double tap L3 to hear your mana.",
                  "Los apoyos también suben el coste de una habilidad. Si una habilidad cuesta más maná del que tienes, no se puede usar; toca L3 dos veces para oír tu maná."),
            },
        },
        new Lesson
        {
            Title = T("Putting skills on your buttons", "Poner habilidades en tus botones"),
            Pages = new[]
            {
                T("A skill gem in your gear gives you its skill, but you use it from a button. The game's skill binding mode, opened with R3 in play, puts skills on buttons. The mod says Skill binding, and the game's prompts for it.",
                  "Una gema de habilidad en tu equipo te da su habilidad, pero la usas desde un botón. El modo de asignación de habilidades del juego, que se abre con R3 al jugar, pone habilidades en botones. El mod dice Asignación de habilidades, y las indicaciones del juego para él."),
                T("Move with the right stick: each button's slot is said with its skill, or empty, such as X: Burning Arrow, or D-pad up: empty.",
                  "Muévete con el stick derecho: se dice la ranura de cada botón con su habilidad, o vacía, como X: Flecha ardiente, o Cruceta arriba: vacía."),
                T("Select a slot to open the list of skills you can put there. Each is read with its place, such as 2 of 4, and everything about it: damage, speed, cost and what it does. Select one to put it on that button.",
                  "Selecciona una ranura para abrir la lista de habilidades que puedes poner ahí. Cada una se lee con su lugar, como 2 de 4, y todo sobre ella: daño, velocidad, coste y lo que hace. Selecciona una para ponerla en ese botón."),
                T("If you hear that it is the only skill you can put there, you need more skill gems socketed in your gear first. Go back with the game's button; the mod says Skill binding closed.",
                  "Si oyes que es la única habilidad que puedes poner ahí, primero necesitas más gemas de habilidad engarzadas en tu equipo. Vuelve con el botón del juego; el mod dice Asignación de habilidades cerrada."),
            },
        },
        new Lesson
        {
            Title = T("Vendors: buying and selling", "Vendedores: comprar y vender"),
            Pages = new[]
            {
                T("Talk to a character who trades; the dialogue's choices are read, among them buying and selling. Each window is read as you move in it, with its tabs.",
                  "Habla con un personaje que comercia; se leen las opciones del diálogo, entre ellas comprar y vender. Cada ventana se lee al moverte por ella, con sus pestañas."),
                T("Buying: move through the vendor's items; each is read with its description, then its price and whether you can pay it, such as Price: 1 Orb of Transmutation, you have 3, or you have 1, 2 more needed. Hold L3 and push the right stick up to hear the price again.",
                  "Comprar: recorre los objetos del vendedor; cada uno se lee con su descripción, luego su precio y si puedes pagarlo, como Precio: 1 Orbe de transmutación, tienes 3, o tienes 1, faltan 2. Mantén L3 y empuja el stick derecho arriba para oír el precio otra vez."),
                T("Hold L3 and push the right stick down for all the currency you have, and your gold. The count is your bag and the stash tab you have open; a purchase can also use stash tabs you opened earlier. Bought items are said as Received, with where they went in your bag.",
                  "Mantén L3 y empuja el stick derecho abajo para oír toda la moneda que tienes, y tu oro. La cuenta es tu bolsa y la pestaña del alijo que tienes abierta; una compra también puede usar pestañas del alijo que abriste antes. Los objetos comprados se dicen como Recibido, con dónde quedaron en tu bolsa."),
                T("Selling: in the sell window, items you put in are said as Handed over, and what the vendor offers in return is said as it changes, such as Vendor offers: 3 Alteration Shard. Accept the trade with the game's button, and what you are paid is said as Received.",
                  "Vender: en la ventana de venta, los objetos que pones se dicen como Entregado, y lo que el vendedor ofrece a cambio se dice al cambiar, como El vendedor ofrece: 3 Fragmento de alteración. Acepta el trato con el botón del juego, y lo que te pagan se dice como Recibido."),
                T("Reading prices has not been tried in the game yet. If a price is not said, the mod writes what it found on screen to its log; send it after a visit to a vendor.",
                  "La lectura de precios aún no se ha probado en el juego. Si no se dice un precio, el mod escribe en su registro lo que encontró en pantalla; envíalo después de visitar a un vendedor."),
                T("Early on, selling magic and rare items gives currency shards, and an item with red, green and blue sockets linked together gives a Chromatic Orb. Keep anything with good mods for your stash.",
                  "Al principio, vender objetos mágicos y raros da fragmentos de moneda, y un objeto con engarces rojo, verde y azul enlazados da un Orbe cromático. Guarda en tu alijo lo que tenga buenos modificadores."),
            },
        },
        new Lesson
        {
            Title = T("The stash", "El alijo"),
            Pages = new[]
            {
                T("The stash in every town is shared by all your characters in the league. It clunks from its side, and the scanner's Objects lead you to it.",
                  "El alijo de cada ciudad lo comparten todos tus personajes de la liga. Suena desde su lado, y los Objetos del entorno del escáner te llevan a él."),
                T("Open it with the game's button: each tab is said as you switch to it. Items in a tab are read like the bag's as you move to them.",
                  "Ábrelo con el botón del juego: cada pestaña se dice al cambiar a ella. Los objetos de una pestaña se leen como los de la bolsa al moverte a ellos."),
                T("Moving an item between the bag and the stash is said as Received or Handed over. Keep currency, good items and gems for later characters there.",
                  "Mover un objeto entre la bolsa y el alijo se dice como Recibido o Entregado. Guarda ahí moneda, buenos objetos y gemas para personajes futuros."),
            },
        },
        new Lesson
        {
            Title = T("Quest rewards", "Recompensas de misión"),
            Pages = new[]
            {
                T("When a quest is done, the character who gave it has a reward: walk to them, and the scanner says they have a quest. Talk, and choose the reward option.",
                  "Cuando una misión está hecha, el personaje que la dio tiene una recompensa: camina hasta él, y el escáner dice que tiene una misión. Habla y elige la opción de recompensa."),
                T("The reward window is read as you move through it: each choice, often a skill gem, with its full description. Tap L3 through its lines to compare.",
                  "La ventana de recompensas se lee al recorrerla: cada opción, a menudo una gema de habilidad, con su descripción completa. Toca L3 por sus líneas para comparar."),
                T("Take one with the game's button; it is said as Received, with where it went in your bag. Some quests give a Book of Skill instead, a passive point, or a choice of passive bonuses.",
                  "Coge una con el botón del juego; se dice como Recibido, con dónde quedó en tu bolsa. Algunas misiones dan en su lugar un Libro de habilidad, un punto de pasiva, o una elección de bonificaciones pasivas."),
            },
        },
        new Lesson
        {
            Title = T("Running a map quickly", "Hacer un mapa rápido"),
            Pages = new[]
            {
                T("The endgame rewards speed: the more maps you finish in an hour, the more you find. These are the mod's tools for going fast.",
                  "El final del juego premia la velocidad: cuantos más mapas terminas en una hora, más encuentras. Estas son las herramientas del mod para ir rápido."),
                T("Before: read the map in your bag. Its first line gives its tier, Atlas Drops, rarity and pack size, and warns of mods that are dangerous for many builds. Skip or change a map that warns of something your build cannot handle.",
                  "Antes: lee el mapa en tu bolsa. Su primera línea da su nivel, botín del Atlas, rareza y tamaño de grupo, y avisa de modificadores peligrosos para muchas configuraciones. Salta o cambia un mapa que avise de algo que tu configuración no aguanta."),
                T("At the map device, the mod says what is in it as you put things in: the map and each Thread. Activate it with the game's button; its portals are under Exits.",
                  "En el dispositivo de mapas, el mod dice lo que tiene al poner cosas: el mapa y cada Hilo. Actívalo con el botón del juego; sus portales están en Salidas."),
                T("In the map, use explore mode: choose Unexplored in the scanner, take the first entry, the biggest, and triple tap L3. When you get there the guide goes on to the next unexplored ground by itself, until it says Area explored.",
                  "En el mapa, usa el modo exploración: elige Sin explorar en el escáner, toma la primera entrada, la más grande, y toca L3 tres veces. Al llegar, la guía sigue sola al siguiente terreno sin explorar, hasta que dice Área explorada."),
                T("Loot follows your loot filter, so only what the game shows makes a sound. A stricter loot filter in the game's options means fewer sounds and less time picking things up.",
                  "El botín sigue tu filtro de botín, así que solo suena lo que el juego muestra. Un filtro de botín más estricto en las opciones del juego significa menos sonidos y menos tiempo recogiendo cosas."),
                T("The map boss is an enemy with its life said as it falls. As the map's monsters run out, the mod says how many are left. When you are done, a portal back is under Exits.",
                  "El jefe del mapa es un enemigo cuya vida se dice a medida que baja. Cuando se acaban los monstruos del mapa, el mod dice cuántos quedan. Al terminar, un portal de vuelta está en Salidas."),
            },
        },
        new Lesson
        {
            Title = T("The character sheet and your defences", "La hoja de personaje y tus defensas"),
            Pages = new[]
            {
                T("The character sheet lists everything about your character. Open it with the game's button, then hold L3 and use the right stick: up and down go stat by stat, left and right go section by section.",
                  "La hoja de personaje lo enumera todo sobre tu personaje. Ábrela con el botón del juego, luego mantén L3 y usa el stick derecho: arriba y abajo van estadística por estadística, izquierda y derecha sección por sección."),
                T("What matters most early: your life, your fire, cold and lightning resistances, which should reach 75 percent, and your damage per second for your main skill.",
                  "Lo más importante al principio: tu vida, tus resistencias al fuego, al frío y al rayo, que deberían llegar al 75 por ciento, y el daño por segundo de tu habilidad principal."),
                T("After Act 5 and Act 10 your resistances drop for good, so check them after each, and look for gear with resistances, read in each item's mods.",
                  "Después del acto 5 y del acto 10 tus resistencias bajan para siempre, así que revísalas después de cada uno, y busca equipo con resistencias, que se leen en los modificadores de cada objeto."),
            },
        },
    };
}
