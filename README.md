**Adaptive Bot Culling — AI & Animation Optimizer**

This mod began with an investigation into SPT’s CPU bottlenecks. Using ILSpy, I decompiled and traced EFT’s more raw managed assemblies, gradually disabling parts of the AI, animation, and asset-processing systems to measure their impact. Removing active bots eliminated most of the CPU bottleneck, which led me to discover that distant and occluded bots continued running their AI brains, bone updates, and body animations at full capacity.

*Let me know in the comments the performance gains you get*

Adaptive Bot Culling extends EFT’s existing culling system to pause this unnecessary processing when bots are hidden or occluded. This can provide meaningful performance improvements, especially on larger maps where many bots are spread across distant or obstructed areas.

***Warning:*** *These gains come at the cost of some immersion and normal AI behavior. Culled bots may stop roaming, reacting, or fighting other AI until you move close enough for them to become active again.*

*Compatibility Notice: This mod has only been tested with SPT’s default AI. Mods that modify AI behavior may conflict with it or behave unexpectedly.*

*Performance Notice: Results will vary significantly depending on your hardware, map, bot count, and whether your game is CPU-limited. On my CPU-bottlenecked system, performance increased from approximately 40–55 FPS to 70–90 FPS. Systems limited by the GPU, or situations where most bots remain visible and active, may experience smaller gains or none at all.*

**Usage**

Use the F12 menu to turn it on and off

Performance gains depend on the situation. The mod provides little or no benefit when every bot is visible or otherwise not being culled. Generally, the larger the map and the more separated its bot population, the greater the potential improvement.

Contact: Hysocs568@gmail.com
