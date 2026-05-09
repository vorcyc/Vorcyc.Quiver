using NAudio.Wave;
using TorchSharp;
using static TorchSharp.torch;

namespace NiubiServer.Inferencers;

/// <summary>
/// 独立的语音/音乐检测器。
/// 使用 MIT/ast-finetuned-audioset-10-10-0.4593 (AudioSet 527类) 的 TorchScript 模型。
/// 模型内部已包含 fbank 预处理，C# 侧只需传原始 16 kHz PCM 波形。
///
/// 判断逻辑：
///   - 取 Speech(0)、Singing(27)、Music(137) 三类概率
///   - speech > music+singing → 语音，否则 → 音乐
///
/// 前置：先运行 export_ast_model.py 导出 F:\ast_audioset.pt
/// </summary>
static class AudioClassifier
{
    const string FbankModelPath       = @"F:\ast_fbank.pt";
    const string TransformerModelPath = @"F:\ast_transformer.pt";
    const string MusicFolder = @"G:\歌曲宝\1-100000\1-5000";
    static string OutputCsv  => Path.Combine(@"F:\", new DirectoryInfo(MusicFolder).Name + "_speech_music.csv");
    const int SampleRate     = 16000;
    const int MaxSeconds     = 10;
    const float Threshold    = 0.3f;  // sigmoid 阈值，调高可减少标签数量

    // AudioSet 标签索引（与 config.json label2id 一致）
    internal static readonly (int idx, string name)[] WatchedLabels =
    [
        (  0, "语音(Speech)"),
        (  1, "男性语音(Male speech)"),
        (  2, "女性语音(Female speech)"),
        (  3, "儿童声音(Child speech)"),
        (  4, "对话(Conversation)"),
        (  5, "独白(Narration)"),
        (  6, "演讲(Speech)"),
        (  7, "语音合成(Speech synthesizer)"),
        (  8, "呐喊(Shout)"),
        (  9, "喊叫(Bellow)"),
        ( 10, "呼喊(Whoop)"),
        ( 11, "大喊(Yell)"),
        ( 12, "儿童喊叫(Children shouting)"),
        ( 13, "笑声(Laughter)"),
        ( 14, "尖叫(Screaming)"),
        ( 15, "耳语(Whispering)"),
        ( 16, "掌声(Applause)"),
        ( 17, "欢呼(Cheer)"),
        ( 18, "哭泣(Crying)"),
        ( 19, "窃笑(Snicker)"),
        ( 20, "叹息(Sigh)"),
        ( 21, "歌唱(Singing)"),
        ( 22, "合唱(Choir)"),
        ( 23, "呜咽(Whimper)"),
        ( 24, "哀鸣(Wail, moan)"),
        ( 25, "呻吟(Groan)"),
        ( 26, "叹气(Sigh)"),
        ( 27, "演唱(Singing)"),
        ( 28, "人声(Human voice)"),
        ( 29, "约德尔(Yodeling)"),
        ( 30, "说唱(Rapping)"),
        ( 31, "嗡嗡声(Humming)"),
        ( 32, "婴儿啼哭(Baby cry)"),
        ( 33, "婴儿咯咯笑(Baby laughter)"),
        ( 34, "打嗝(Burping)"),
        ( 35, "合成人声(Synthetic singing)"),
        ( 36, "打哈欠(Yawning)"),
        ( 37, "咳嗽(Cough)"),
        ( 38, "喉咙清嗓(Throat clearing)"),
        ( 39, "呼吸(Breathing)"),
        ( 40, "口哨(Whistling)"),
        ( 41, "鼾声(Snoring)"),
        ( 42, "喘息(Wheeze)"),
        ( 43, "鼾睡(Snoring)"),
        ( 44, "心跳(Heartbeat)"),
        ( 45, "脚步声(Footsteps)"),
        ( 46, "哼鸣(Snort)"),
        ( 47, "打嚏(Sneeze)"),
        ( 48, "清喉(Throat clearing)"),
        ( 49, "喷嚏(Sneeze)"),
        ( 50, "嗅气(Sniff)"),
        ( 51, "跑步(Run)"),
        ( 52, "脚步拖拉(Shuffle)"),
        ( 53, "走路(Walk, footsteps)"),
        ( 54, "爬楼梯(Staircase)"),
        ( 55, "咀嚼(Chewing)"),
        ( 56, "咬食(Biting)"),
        ( 57, "胃肠鸣(Stomach rumble)"),
        ( 58, "心跳(Heartbeat)"),
        ( 59, "手势(Hands)"),
        ( 60, "狗叫(Dog)"),
        ( 61, "狗吠(Bark)"),
        ( 62, "狗嚎(Howl)"),
        ( 63, "狗哀鸣(Whimper dog)"),
        ( 64, "狗喘气(Panting)"),
        ( 65, "狗低吼(Growling)"),
        ( 66, "猫叫(Cat)"),
        ( 67, "猫喵(Meow)"),
        ( 68, "猫呜(Purr)"),
        ( 69, "猫嘶吼(Hiss)"),
        ( 70, "猫喵叫(Caterwaul)"),
        ( 71, "家禽(Domestic animals)"),
        ( 72, "马嘶(Neigh)"),
        ( 73, "马蹄(Clip-clop)"),
        ( 74, "猪叫(Pig)"),
        ( 75, "牛叫(Cattle, bovinae)"),
        ( 76, "犬吠(Yip)"),
        ( 77, "羊叫(Sheep)"),
        ( 78, "鸡叫(Chicken, rooster)"),
        ( 79, "公鸡啼叫(Rooster)"),
        ( 80, "犬哀鸣(Whimper dog)"),
        ( 81, "鸟叫(Bird)"),
        ( 82, "鸟鸣(Bird vocalization)"),
        ( 83, "鸣禽(Songbird)"),
        ( 84, "知更鸟(Robin)"),
        ( 85, "鸟翅扑打(Bird flight)"),
        ( 86, "鸭叫(Duck)"),
        ( 87, "鹅叫(Goose)"),
        ( 88, "鸭嘎嘎(Quacking)"),
        ( 89, "鸮鸣(Owl)"),
        ( 90, "鸦鸣(Crow)"),
        ( 91, "青蛙叫(Frog)"),
        ( 92, "蛇嘶声(Hiss)"),
        ( 93, "昆虫鸣叫(Insect)"),
        ( 94, "蟋蟀(Cricket)"),
        ( 95, "蝉鸣(Insect)"),
        ( 96, "蜜蜂嗡嗡(Bee, wasp)"),
        ( 97, "羊叫(Sheep)"),
        ( 98, "狮吼(Roaring cats)"),
        ( 99, "老虎吼(Tiger)"),
        (100, "豹吼(Growling)"),
        (101, "大象鸣叫(Elephant)"),
        (102, "火鸡叫(Turkey)"),
        (103, "猴叫(Primate)"),
        (104, "熊吼(Bear)"),
        (105, "蝙蝠声(Bat)"),
        (106, "海豚声(Dolphin)"),
        (107, "鸟群(Flock of birds)"),
        (108, "野生动物(Wild animals)"),
        (109, "海浪(Ocean)"),
        (110, "海鸥(Gull, seagull)"),
        (111, "打雷(Thunder)"),
        (112, "下雨(Rain)"),
        (113, "雷暴(Thunderstorm)"),
        (114, "鸟鸣(Squawk)"),
        (115, "野鸟(Wild bird)"),
        (116, "鸟(Bird)"),
        (117, "猫头鹰(Owl)"),
        (118, "蝈蝈(Grasshopper)"),
        (119, "蝉(Cicada)"),
        (120, "蚊子(Mosquito)"),
        (121, "苍蝇(Fly, housefly)"),
        (122, "蟑螂(Cockroach)"),
        (123, "蜜蜂(Bee)"),
        (124, "水声(Water)"),
        (125, "流水(Stream)"),
        (126, "瀑布(Waterfall)"),
        (127, "海浪(Waves, surf)"),
        (128, "雨声(Rain)"),
        (129, "雷声(Thunder)"),
        (130, "风声(Wind)"),
        (131, "火声(Fire)"),
        (132, "自然声音(Sounds of things)"),
        (133, "雪地声(Snow)"),
        (134, "蛇声(Snake)"),
        (135, "丛林声(Jungle)"),
        (136, "鲸鱼声(Whale vocalization)"),
        (137, "音乐(Music)"),
        (138, "乐器(Musical instrument)"),
        (139, "拨弦乐器(Plucked string instrument)"),
        (140, "吉他(Guitar)"),
        (141, "原声吉他(Acoustic guitar)"),
        (142, "钢弦吉他(Steel guitar)"),
        (143, "拨片吉他(Banjo)"),
        (144, "滑棒吉他(Steel guitar, slide guitar)"),
        (145, "点弦技巧(Tapping guitar)"),
        (146, "拨弦(Strum)"),
        (147, "低音吉他(Bass guitar)"),
        (148, "西塔琴(Sitar)"),
        (149, "曼陀林(Mandolin)"),
        (150, "齐特琴(Zither)"),
        (151, "尤克里里(Ukulele)"),
        (152, "键盘乐器(Keyboard musical instrument)"),
        (153, "钢琴(Piano)"),
        (154, "电钢琴(Electric piano)"),
        (155, "风琴(Organ)"),
        (156, "电子风琴(Electronic organ)"),
        (157, "教堂风琴(Church organ)"),
        (158, "合成器(Synthesizer)"),
        (159, "采样器(Sampler)"),
        (160, "电脑键盘音(Computer keyboard)"),
        (161, "打击乐器(Percussion)"),
        (162, "鼓(Drum)"),
        (163, "鼓机(Drum machine)"),
        (164, "鼓独奏(Drum solo)"),
        (165, "军鼓(Snare drum)"),
        (166, "通通鼓(Tom-tom drum)"),
        (167, "低音鼓(Bass drum)"),
        (168, "踩镲(Hi-hat)"),
        (169, "定音鼓(Timpani)"),
        (170, "塔布拉鼓(Tabla)"),
        (171, "邦哥鼓(Bongo drum)"),
        (172, "康加鼓(Conga drum)"),
        (173, "木鱼(Wood block)"),
        (174, "铃鼓(Tambourine)"),
        (175, "沙锤(Rattle)"),
        (176, "铃铛(Cowbell)"),
        (177, "锣(Gong)"),
        (178, "管钟(Tubular bells)"),
        (179, "木琴(Marimba, xylophone)"),
        (180, "颤音琴(Vibraphone)"),
        (181, "铁琴(Glockenspiel)"),
        (182, "颤音琴(Vibraphone)"),
        (183, "钢鼓(Steelpan)"),
        (184, "爵士鼓(Drum kit)"),
        (185, "管弦乐(Orchestra)"),
        (186, "铜管乐器(Brass instrument)"),
        (187, "小号(Trumpet)"),
        (188, "长号(Trombone)"),
        (189, "法国号(French horn)"),
        (190, "弦乐组(String section)"),
        (191, "小提琴(Violin, fiddle)"),
        (192, "中提琴(Viola)"),
        (193, "大提琴(Cello)"),
        (194, "低音提琴(Double bass)"),
        (195, "木管乐器(Wind instrument)"),
        (196, "长笛(Flute)"),
        (197, "单簧管(Clarinet)"),
        (198, "双簧管(Oboe)"),
        (199, "巴松管(Bassoon)"),
        (200, "萨克斯风(Saxophone)"),
        (201, "口琴(Harmonica)"),
        (202, "风笛(Bagpipes)"),
        (203, "迪吉里杜管(Didgeridoo)"),
        (204, "音叉(Tuning fork)"),
        (205, "风铃(Chime)"),
        (206, "风铃(Wind chime)"),
        (207, "铃声(Bell)"),
        (208, "教堂钟(Church bell)"),
        (209, "敲钟(Jingle bell)"),
        (210, "丁零声(Bicycle bell)"),
        (211, "鸣笛(Cowbell)"),
        (212, "羊角号(Shofar)"),
        (213, "特雷门(Theremin)"),
        (214, "颂钵(Singing bowl)"),
        (215, "刮擦技法(Scratching performance)"),
        (216, "打碟(Turntablism)"),
        (217, "拨弦(Pizzicato)"),
        (218, "弓奏(Bowed string instrument)"),
        (219, "竖琴(Harp)"),
        (220, "大键琴(Harpsichord)"),
        (221, "原声音乐(Acoustic music)"),
        (222, "节拍(Beat)"),
        (223, "低音线(Bass line)"),
        (224, "旋律(Melody)"),
        (225, "流行音乐(Pop music)"),
        (226, "嘻哈(Hip hop music)"),
        (227, "灵魂乐(Soul music)"),
        (228, "爵士乐(Jazz)"),
        (229, "放克(Funk)"),
        (230, "摇摆乐(Swing music)"),
        (231, "蓝调(Blues)"),
        (232, "节奏蓝调(Rhythm and blues)"),
        (233, "摇滚(Rock music)"),
        (234, "印地摇滚(Indie rock)"),
        (235, "另类摇滚(Alternative rock)"),
        (236, "朋克(Punk rock)"),
        (237, "重金属(Heavy metal)"),
        (238, "死亡金属(Death metal)"),
        (239, "硬核朋克(Hardcore punk)"),
        (240, "电子音乐(Electronic music)"),
        (241, "电子舞曲(Techno)"),
        (242, "浩室音乐(House music)"),
        (243, "科技浩室(Tech house)"),
        (244, "深浩室(Deep house)"),
        (245, "大屋(Eurodance)"),
        (246, "迪斯科(Disco)"),
        (247, "迷幻电子(Trance music)"),
        (248, "鼓打贝斯(Drum and bass)"),
        (249, "电子体(Electronica)"),
        (250, "氛围音乐(Ambient music)"),
        (251, "旅鼓(Trip hop)"),
        (252, "迷幻(Psychedelic)"),
        (253, "新浪潮(New-age music)"),
        (254, "声乐(Vocal music)"),
        (255, "合唱(Chant)"),
        (256, "贝斯音乐(Beatboxing)"),
        (257, "古典音乐(Classical music)"),
        (258, "歌剧(Opera)"),
        (259, "钢琴独奏(Piano solo)"),
        (260, "室内乐(Chamber music)"),
        (261, "雷鬼(Reggae)"),
        (262, "达布(Dub)"),
        (263, "斯卡(Ska)"),
        (264, "传统音乐(Traditional music)"),
        (265, "民谣(Folk music)"),
        (266, "歌曲(Song)"),
        (267, "背景音乐(Background music)"),
        (268, "主题音乐(Theme music)"),
        (269, "广告音乐(Jingle music)"),
        (270, "原声音乐(Soundtrack music)"),
        (271, "情绪音乐(Emotional music)"),
        (272, "游戏音乐(Video game music)"),
        (273, "儿童音乐(Children's music)"),
        (274, "圣诞音乐(Christmas music)"),
        (275, "婚礼音乐(Wedding music)"),
        (276, "生日歌(Happy birthday)"),
        (277, "国歌(National anthem)"),
        (278, "军乐(March)"),
        (279, "温柔音乐(Tender music)"),
        (280, "怀旧音乐(Lullaby)"),
        (281, "摇篮曲(Lullaby)"),
        (282, "噪音摇滚(Noise rock)"),
        (283, "风声(Wind)"),
        (284, "沙沙声(Rustling leaves)"),
        (285, "风噪(Wind noise)"),
        (286, "雷暴(Thunderstorm)"),
        (287, "雷声(Thunder)"),
        (288, "水声(Water)"),
        (289, "雨声(Rain on surface)"),
        (290, "雨滴(Raindrop)"),
        (291, "小雨(Drizzle)"),
        (292, "溪流(Stream)"),
        (293, "瀑布(Waterfall)"),
        (294, "泡泡声(Gurgling)"),
        (295, "海浪(Waves, surf)"),
        (296, "蒸汽(Steam)"),
        (297, "气泡(Bubble)"),
        (298, "火燃烧(Fire)"),
        (299, "篝火(Crackle)"),
        (300, "车辆(Vehicle)"),
        (301, "船(Boat, Water vehicle)"),
        (302, "帆船(Sailboat, sailing ship)"),
        (303, "划艇(Rowboat)"),
        (304, "汽艇(Motorboat, speedboat)"),
        (305, "轮船(Ship)"),
        (306, "螺旋桨船(Propeller, airscrew)"),
        (307, "汽车(Car)"),
        (308, "汽车喇叭(Vehicle horn)"),
        (309, "车鸣(Toot)"),
        (310, "汽车报警(Car alarm)"),
        (311, "车窗(Power windows)"),
        (312, "打滑(Skidding)"),
        (313, "轮胎鸣叫(Tire squeal)"),
        (314, "汽车驶过(Car passing by)"),
        (315, "赛车(Race car)"),
        (316, "卡车(Truck)"),
        (317, "气刹(Air brake)"),
        (318, "卡车喇叭(Air horn)"),
        (319, "倒车提示音(Reversing beeps)"),
        (320, "冰淇淋车(Ice cream truck)"),
        (321, "公共汽车(Bus)"),
        (322, "紧急车辆(Emergency vehicle)"),
        (323, "警车(Police car siren)"),
        (324, "救护车(Ambulance siren)"),
        (325, "消防车(Fire engine siren)"),
        (326, "摩托车(Motorcycle)"),
        (327, "交通噪声(Traffic noise)"),
        (328, "轨道交通(Rail transport)"),
        (329, "火车(Train)"),
        (330, "火车汽笛(Train whistle)"),
        (331, "火车喇叭(Train horn)"),
        (332, "火车车厢(Railroad car)"),
        (333, "车轮鸣叫(Train wheels squealing)"),
        (334, "地铁(Subway)"),
        (335, "飞机(Aircraft)"),
        (336, "飞机发动机(Aircraft engine)"),
        (337, "喷气发动机(Jet engine)"),
        (338, "螺旋桨(Propeller)"),
        (339, "直升机(Helicopter)"),
        (340, "固定翼飞机(Fixed-wing aircraft)"),
        (341, "自行车(Bicycle)"),
        (342, "滑板(Skateboard)"),
        (343, "发动机(Engine)"),
        (344, "高频发动机(Light engine)"),
        (345, "牙钻(Dental drill)"),
        (346, "割草机(Lawn mower)"),
        (347, "链锯(Chainsaw)"),
        (348, "中频发动机(Medium engine)"),
        (349, "低频发动机(Heavy engine)"),
        (350, "发动机爆震(Engine knocking)"),
        (351, "发动机启动(Engine starting)"),
        (352, "怠速(Idling)"),
        (353, "加速(Accelerating)"),
        (354, "门(Door)"),
        (355, "门铃(Doorbell)"),
        (356, "叮咚(Ding-dong)"),
        (357, "推拉门(Sliding door)"),
        (358, "关门声(Slam)"),
        (359, "敲门(Knock)"),
        (360, "轻叩(Tap)"),
        (361, "吱呀(Squeak)"),
        (362, "柜门(Cupboard open or close)"),
        (363, "抽屉(Drawer open or close)"),
        (364, "碗碟(Dishes, pots, and pans)"),
        (365, "餐具(Cutlery, silverware)"),
        (366, "切菜(Chopping food)"),
        (367, "煎炸(Frying food)"),
        (368, "微波炉(Microwave oven)"),
        (369, "搅拌机(Blender)"),
        (370, "水龙头(Water tap)"),
        (371, "水槽(Sink)"),
        (372, "浴缸(Bathtub)"),
        (373, "吹风机(Hair dryer)"),
        (374, "马桶冲水(Toilet flush)"),
        (375, "牙刷(Toothbrush)"),
        (376, "电动牙刷(Electric toothbrush)"),
        (377, "吸尘器(Vacuum cleaner)"),
        (378, "拉链(Zipper)"),
        (379, "钥匙串(Keys jangling)"),
        (380, "硬币(Coin dropping)"),
        (381, "剪刀(Scissors)"),
        (382, "电动剃须刀(Electric shaver)"),
        (383, "洗牌(Shuffling cards)"),
        (384, "打字(Typing)"),
        (385, "打字机(Typewriter)"),
        (386, "电脑键盘(Computer keyboard)"),
        (387, "书写(Writing)"),
        (388, "闹钟(Alarm)"),
        (389, "电话(Telephone)"),
        (390, "电话铃(Telephone bell ringing)"),
        (391, "来电铃声(Ringtone)"),
        (392, "拨号音(Telephone dialing)"),
        (393, "拨号长音(Dial tone)"),
        (394, "占线音(Busy signal)"),
        (395, "闹钟(Alarm clock)"),
        (396, "警报(Siren)"),
        (397, "防空警报(Civil defense siren)"),
        (398, "蜂鸣器(Buzzer)"),
        (399, "烟雾报警(Smoke detector)"),
        (400, "火警(Fire alarm)"),
        (401, "雾号(Foghorn)"),
        (402, "哨声(Whistle)"),
        (403, "蒸汽哨(Steam whistle)"),
        (404, "机械(Mechanisms)"),
        (405, "棘齿(Ratchet, pawl)"),
        (406, "时钟(Clock)"),
        (407, "滴答(Tick)"),
        (408, "滴答声(Tick-tock)"),
        (409, "齿轮(Gears)"),
        (410, "滑轮(Pulleys)"),
        (411, "缝纫机(Sewing machine)"),
        (412, "电风扇(Mechanical fan)"),
        (413, "空调(Air conditioning)"),
        (414, "收银机(Cash register)"),
        (415, "打印机(Printer)"),
        (416, "相机(Camera)"),
        (417, "单反相机(Single-lens reflex camera)"),
        (418, "工具(Tools)"),
        (419, "锤子(Hammer)"),
        (420, "气锤(Jackhammer)"),
        (421, "锯(Sawing)"),
        (422, "锉(Filing)"),
        (423, "砂纸(Sanding)"),
        (424, "电动工具(Power tool)"),
        (425, "钻(Drill)"),
        (426, "爆炸(Explosion)"),
        (427, "枪声(Gunshot, gunfire)"),
        (428, "机枪(Machine gun)"),
        (429, "连射(Fusillade)"),
        (430, "炮声(Artillery fire)"),
        (431, "玩具枪(Cap gun)"),
        (432, "烟花(Fireworks)"),
        (433, "鞭炮(Firecracker)"),
        (434, "爆裂(Burst, pop)"),
        (435, "火山喷发(Eruption)"),
        (436, "轰鸣(Boom)"),
        (437, "木头(Wood)"),
        (438, "劈砍(Chop)"),
        (439, "裂碎(Splinter)"),
        (440, "裂开(Crack)"),
        (441, "玻璃(Glass)"),
        (442, "叮当(Chink, clink)"),
        (443, "破碎(Shatter)"),
        (444, "液体(Liquid)"),
        (445, "溅水(Splash, splatter)"),
        (446, "晃动(Slosh)"),
        (447, "挤压(Squish)"),
        (448, "滴水(Drip)"),
        (449, "倒水(Pour)"),
        (450, "细流(Trickle, dribble)"),
        (451, "涌出(Gush)"),
        (452, "注水(Fill with liquid)"),
        (453, "喷雾(Spray)"),
        (454, "泵(Pump liquid)"),
        (455, "搅拌(Stir)"),
        (456, "沸腾(Boiling)"),
        (457, "声纳(Sonar)"),
        (458, "箭矢(Arrow)"),
        (459, "呼啸(Whoosh)"),
        (460, "重击(Thump, thud)"),
        (461, "撞击(Thunk)"),
        (462, "电子调谐器(Electronic tuner)"),
        (463, "效果器(Effects unit)"),
        (464, "合唱效果(Chorus effect)"),
        (465, "篮球弹跳(Basketball bounce)"),
        (466, "砰声(Bang)"),
        (467, "拍打(Slap, smack)"),
        (468, "猛打(Whack, thwack)"),
        (469, "撞碎(Smash, crash)"),
        (470, "断裂(Breaking)"),
        (471, "弹跳(Bouncing)"),
        (472, "鞭打(Whip)"),
        (473, "拍动(Flap)"),
        (474, "刮擦(Scratch)"),
        (475, "刮(Scrape)"),
        (476, "摩擦(Rub)"),
        (477, "滚动(Roll)"),
        (478, "压碎(Crushing)"),
        (479, "皱纸(Crumpling)"),
        (480, "撕裂(Tearing)"),
        (481, "哔声(Beep, bleep)"),
        (482, "乒声(Ping)"),
        (483, "叮声(Ding)"),
        (484, "铿锵(Clang)"),
        (485, "嗥叫(Squeal)"),
        (486, "吱呀(Creak)"),
        (487, "沙沙(Rustle)"),
        (488, "嗡嗡(Whir)"),
        (489, "噼里啪啦(Clatter)"),
        (490, "嗞嗞(Sizzle)"),
        (491, "咔嗒(Clicking)"),
        (492, "咔哒咔哒(Clickety-clack)"),
        (493, "隆隆(Rumble)"),
        (494, "扑通(Plop)"),
        (495, "叮铃(Jingle, tinkle)"),
        (496, "嗡声(Hum)"),
        (497, "嗡鸣(Zing)"),
        (498, "弹簧声(Boing)"),
        (499, "嘎吱(Crunch)"),
        (500, "静音(Silence)"),
        (501, "正弦波(Sine wave)"),
        (502, "谐波(Harmonic)"),
        (503, "啁啾音(Chirp tone)"),
        (504, "音效(Sound effect)"),
        (505, "脉冲(Pulse)"),
        (506, "室内小空间(Inside, small room)"),
        (507, "室内大厅(Inside, large room or hall)"),
        (508, "公共空间(Inside, public space)"),
        (509, "城市户外(Outside, urban)"),
        (510, "乡村户外(Outside, rural)"),
        (511, "混响(Reverberation)"),
        (512, "回声(Echo)"),
        (513, "噪声(Noise)"),
        (514, "环境噪声(Environmental noise)"),
        (515, "静电噪声(Static)"),
        (516, "电源嗡嗡(Mains hum)"),
        (517, "失真(Distortion)"),
        (518, "侧音(Sidetone)"),
        (519, "嘈杂(Cacophony)"),
        (520, "白噪声(White noise)"),
        (521, "粉红噪声(Pink noise)"),
        (522, "律动声(Throbbing)"),
        (523, "震动(Vibration)"),
        (524, "电视(Television)"),
        (525, "广播(Radio)"),
        (526, "现场录音(Field recording)"),
    ];

    // 判断用索引
    const int IdxSpeech  = 0;
    const int IdxSinging = 27;
    const int IdxMusic   = 137;

    static readonly string[] AudioExts =
        [".mp3", ".flac", ".wav", ".m4a", ".ogg", ".aac", ".wma"];

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------
    public static void Run()
    {
        Console.WriteLine("加载语音/音乐检测模型 (AST AudioSet)...");
        using var fbankModel = jit.load(FbankModelPath);                        // CPU
        using var transModel = jit.load(TransformerModelPath, DeviceType.CUDA); // CUDA
        fbankModel.eval();
        transModel.eval();

        var files = Directory
            .EnumerateFiles(MusicFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        Console.WriteLine($"找到 {files.Count} 个音频文件，开始检测...\n");

        using var csv = new StreamWriter(OutputCsv, false, System.Text.Encoding.UTF8);
        csv.WriteLine("目录路径,文件名,命中标签(阈值>" + Threshold + ")");

        int done = 0, failed = 0;
        foreach (var file in files)
        {
            try
            {
                var labels = Predict(file, fbankModel, transModel);
                string name = Path.GetFileName(file);
                string dir  = Path.GetDirectoryName(file) ?? "";
                string labelStr = string.Join(" | ", labels.Select(l => $"{l.name}({l.prob:F2})"));

                csv.WriteLine($"\"{dir}\",\"{name}\",\"{labelStr}\"");
                done++;
                Console.WriteLine($"[{done}/{files.Count}] {dir}\n  -> {labelStr}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  WARNING 跳过 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nOK 完成  成功:{done}  失败:{failed}");
        Console.WriteLine($"结果已保存到: {OutputCsv}");
    }

    // ------------------------------------------------------------------
    // 推理
    // ------------------------------------------------------------------
    static List<(string name, float prob)> Predict(
        string audioPath, jit.ScriptModule fbankModel, jit.ScriptModule transModel)
    {
        float[] samples = LoadAudioMono(audioPath);

        int fixedLen = SampleRate * MaxSeconds;
        float[] buf = new float[fixedLen];
        Array.Copy(samples, buf, Math.Min(samples.Length, fixedLen));

        using var _ = no_grad();
        using var input  = torch.tensor(buf).unsqueeze(0);                   // [1, T]  CPU
        using var fbank  = (Tensor)fbankModel.forward(input);                // [1, 1024, 128] CPU
        using var logits = ((Tensor)transModel.forward(fbank.cuda())).cpu(); // [1, 527] CPU
        using var probs  = torch.sigmoid(logits);                            // [1, 527] 独立概率
        using var flat   = probs.squeeze(0);                                 // [527]

        var result = new List<(string, float)>();
        foreach (var (idx, name) in WatchedLabels)
        {
            float p = flat[idx].item<float>();
            if (p >= Threshold)
                result.Add((name, p));
        }

        // 没有任何超过阈值时，取最高分一个作备用
        if (result.Count == 0)
        {
            int bestIdx = 0; float bestProb = flat[0].item<float>();
            for (int i = 1; i < 527; i++)
            {
                float p = flat[i].item<float>();
                if (p > bestProb) { bestProb = p; bestIdx = i; }
            }
            string label = WatchedLabels.FirstOrDefault(l => l.idx == bestIdx).name ?? $"id={bestIdx}";
            result.Add((label, bestProb));
        }

        return [.. result.OrderByDescending(x => x.Item2)];
    }

    // ------------------------------------------------------------------
    // 加载音频 -> 单声道 16 kHz（fbank 预处理在模型内部完成，不需要归一化）
    // ------------------------------------------------------------------
    static float[] LoadAudioMono(string path)
    {
        using var reader = new AudioFileReader(path);
        var outFmt = new WaveFormat(SampleRate, 16, 1);
        using var resamp = new MediaFoundationResampler(reader, outFmt);
        resamp.ResamplerQuality = 60;
        var provider = resamp.ToSampleProvider();

        var chunk = new float[4096];
        var list  = new List<float>();
        int n;
        while ((n = provider.Read(chunk, 0, chunk.Length)) > 0)
            list.AddRange(chunk[..n]);
        return [.. list];
    }
}
