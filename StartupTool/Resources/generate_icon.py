from PIL import Image, ImageDraw

def create_icon(size=256):
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    
    bg_color = (33, 150, 243)
    center = size // 2
    
    terminal_width = int(size * 0.65)
    terminal_height = int(size * 0.55)
    terminal_x = center - terminal_width // 2
    terminal_y = center - terminal_height // 2
    corner_radius = int(size * 0.08)
    
    draw.rectangle([terminal_x + corner_radius, terminal_y, terminal_x + terminal_width - corner_radius, terminal_y + terminal_height], fill=bg_color)
    draw.rectangle([terminal_x, terminal_y + corner_radius, terminal_x + terminal_width, terminal_y + terminal_height - corner_radius], fill=bg_color)
    draw.ellipse([terminal_x, terminal_y, terminal_x + corner_radius * 2, terminal_y + corner_radius * 2], fill=bg_color)
    draw.ellipse([terminal_x + terminal_width - corner_radius * 2, terminal_y, terminal_x + terminal_width, terminal_y + corner_radius * 2], fill=bg_color)
    draw.ellipse([terminal_x, terminal_y + terminal_height - corner_radius * 2, terminal_x + corner_radius * 2, terminal_y + terminal_height], fill=bg_color)
    draw.ellipse([terminal_x + terminal_width - corner_radius * 2, terminal_y + terminal_height - corner_radius * 2, terminal_x + terminal_width, terminal_y + terminal_height], fill=bg_color)
    
    tab_height = int(size * 0.08)
    tab_width = int(size * 0.4)
    tab_x = center - tab_width // 2
    
    draw.rectangle([tab_x + corner_radius, terminal_y - tab_height, tab_x + tab_width - corner_radius, terminal_y], fill=(26, 115, 232))
    draw.ellipse([tab_x, terminal_y - tab_height, tab_x + corner_radius * 2, terminal_y], fill=(26, 115, 232))
    draw.ellipse([tab_x + tab_width - corner_radius * 2, terminal_y - tab_height, tab_x + tab_width, terminal_y], fill=(26, 115, 232))
    
    dot_size = int(size * 0.03)
    dot_y = terminal_y - tab_height // 2
    draw.ellipse([tab_x + int(size * 0.05) - dot_size // 2, dot_y - dot_size // 2,
                  tab_x + int(size * 0.05) + dot_size // 2, dot_y + dot_size // 2],
                 fill=(255, 99, 132))
    draw.ellipse([tab_x + int(size * 0.05) + dot_size + int(size * 0.02) - dot_size // 2, dot_y - dot_size // 2,
                  tab_x + int(size * 0.05) + dot_size + int(size * 0.02) + dot_size // 2, dot_y + dot_size // 2],
                 fill=(255, 193, 7))
    draw.ellipse([tab_x + int(size * 0.05) + (dot_size + int(size * 0.02)) * 2 - dot_size // 2, dot_y - dot_size // 2,
                  tab_x + int(size * 0.05) + (dot_size + int(size * 0.02)) * 2 + dot_size // 2, dot_y + dot_size // 2],
                 fill=(76, 175, 80))
    
    prompt_size = int(size * 0.18)
    prompt_x = terminal_x + int(size * 0.12)
    prompt_y = terminal_y + int(size * 0.08) + int(size * 0.02)
    draw.text((prompt_x, prompt_y), ">", fill=(255, 255, 255), font_size=prompt_size)
    
    script_line_y = terminal_y + int(size * 0.08) + int(size * 0.28)
    script_length = int(size * 0.25)
    draw.line([prompt_x, script_line_y, prompt_x + script_length, script_line_y],
              fill=(255, 255, 255), width=int(size * 0.02))
    
    power_size = int(size * 0.18)
    power_center_x = int(terminal_x + terminal_width * 0.75)
    power_center_y = terminal_y + terminal_height // 2
    
    power_outer_radius = power_size // 2
    power_inner_radius = power_size // 3
    arc_width = int(power_size * 0.12)
    
    draw.arc([power_center_x - power_outer_radius, power_center_y - power_outer_radius,
              power_center_x + power_outer_radius, power_center_y + power_outer_radius],
             start=300, end=600, fill=(255, 255, 255), width=arc_width)
    
    draw.arc([power_center_x - power_inner_radius, power_center_y - power_inner_radius,
              power_center_x + power_inner_radius, power_center_y + power_inner_radius],
             start=300, end=600, fill=bg_color, width=arc_width)
    
    triangle_size = int(power_size * 0.4125)
    triangle_points = [
        (power_center_x, power_center_y - power_outer_radius - 2),
        (power_center_x - triangle_size * 0.5, power_center_y + triangle_size * 0.5),
        (power_center_x + triangle_size * 0.5, power_center_y + triangle_size * 0.5)
    ]
    draw.polygon(triangle_points, fill=(255, 255, 255))
    
    glow = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    for i in range(8):
        alpha = int(255 * (1 - i / 8) * 0.12)
        r = int(terminal_width * 0.6) + i * int(size * 0.025)
        glow_draw.ellipse([center - r, center - r, center + r, center + r],
                          outline=(33, 150, 243, alpha), width=1)
    
    img = Image.alpha_composite(img, glow)
    
    return img

if __name__ == '__main__':
    icon = create_icon(256)
    icon.save('icon.png')
    print('图标已生成: icon.png')
    
    icon_64 = icon.resize((64, 64), Image.LANCZOS)
    icon_64.save('icon_64.png')
    print('图标已生成: icon_64.png')
    
    icon_48 = icon.resize((48, 48), Image.LANCZOS)
    icon_48.save('icon_48.png')
    print('图标已生成: icon_48.png')
    
    icon_32 = icon.resize((32, 32), Image.LANCZOS)
    icon_32.save('icon_32.png')
    print('图标已生成: icon_32.png')
    
    icon_16 = icon.resize((16, 16), Image.LANCZOS)
    icon_16.save('icon_16.png')
    print('图标已生成: icon_16.png')
    
    print('\n所有图标生成完成!')