/** @jsx jsx **/
import { jsx, css } from "@emotion/react";
import * as React from "react";

export const SimplePreview: React.FunctionComponent<{
    landscape: boolean;
    url: string;
    landscapeWidth: number;
}> = props => {
    // Desktop pixels are much larger, so things come out bloated.
    // For now what we do is make the player & readium think we have twice the pixels,
    // then shrink it all. This gives the controls a more reasonable share of the preview.
    const pixelDensityMultiplier = 2;
    // const scale = 25;
    // const screenWidth = 9 * scale;
    // const screenHeight = 16 * scale;

    let width = (props.landscapeWidth * 9) / 16;
    let height = props.landscapeWidth;
    if (props.landscape) {
        const temp = width;
        width = height;
        height = temp;
    }

    const minBorder = 10; // required on sides and bottom, but not top, since nav bar provides visual border there.
    const rootWidth = props.landscapeWidth + 2 * minBorder;
    const rootHeight = height + minBorder;
    const sidePadding = (rootWidth - width) / 2;
    const topPadding = 0;
    const bottomPadding = topPadding + minBorder;

    return (
        <div>
            <div
                css={css`
                    box-sizing: border-box;
                    height: ${rootHeight}px;
                    width: ${rootWidth}px;
                    padding: ${topPadding}px ${sidePadding}px ${bottomPadding}px
                        ${sidePadding}px;
                    background-color: #2e2e2e;
                    // Prevent parent's scroll bar from being computed based on the unscaled size of our children
                    overflow: hidden;
                `}
            >
                <iframe
                    id="simple-preview"
                    css={css`
                        background-color: #2e2e2e;
                        border: none;
                        flex-shrink: 0; // without this, the height doesn't grow
                        transform-origin: top left;
                        height: ${height * pixelDensityMultiplier}px;
                        width: ${width * pixelDensityMultiplier}px;
                        transform: scale(${1 / pixelDensityMultiplier});
                    `}
                    title="book preview"
                    src={props.url}
                />
            </div>
        </div>
    );
};
